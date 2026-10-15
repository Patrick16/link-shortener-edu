using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Common;
using Common.Models;
using Contracts.Events;
using Infrastructure;
using LinkApi.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkApi.Controllers;

[ApiController]
[Route("[controller]")]
public partial class LinksController(
    DatabaseContext context,
    IEntityCacheService<Link> service,
    IEventDispatcher dispatcher,
    IHashGenerator hashGenerator,
    ICustomAliasCreator aliasCreator,
    ILinkDeletionMarkerStore deletionMarkers,
    ILogger<LinksController> logger) : Controller
{
    private const int PageSize = 50;

    // Closes the PgCat read-your-writes race (see sandbox/docs/scenarios/pitfalls/
    // pgcat-read-your-writes-race.md): RedirectApi's lookup is cache-first, so populating Redis here
    // means an immediate redirect right after create never touches Postgres at all - sidestepping
    // both replica lag AND the async publish/consume gap before ShortenerService persists the row.
    // 60s balances two opposing risks: long enough that normal async-consume latency (including
    // under a RabbitMQ backlog from load testing) finishes well within it, short enough to bound how
    // long this entry can be wrong if the persist genuinely never happens. ShortenerService
    // overwrites this same cache key with the full TTL once it actually persists
    // (LinkCreatedConsumer.cs) - last-write-wins, no coordination needed.
    private const int OptimisticCacheTtlSeconds = 60;

    // Lowercased before this ever runs (see CreateLink) - charset deliberately excludes the mixed
    // case Sha256Base62HashGenerator's alphabet uses, so a custom alias can never collide in format
    // with a generated hash. 3-32 chars, must start alphanumeric (no leading '-'/'_').
    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{2,31}$")]
    private static partial Regex AliasPattern();

    // Grounded in this stack's real single-segment routes, not guessed: RedirectApi resolves every
    // hash/alias at the root ([Route("/")], GET /{hash}), and Scalar API docs are mounted at
    // /scalar (CLAUDE.md: "AuthApi/LinkApi/RedirectApi expose Scalar API docs at /scalar/v1") -
    // health/openapi/api/admin/links are defensive (two-segment today, e.g. /health/live, but cheap
    // to reserve the prefix word against a future single-segment route landing on the same name).
    private static readonly HashSet<string> ReservedAliases =
        new(StringComparer.Ordinal) { "scalar", "health", "openapi", "api", "admin", "links" };

    private readonly DatabaseContext _context = context;
    private readonly IEntityCacheService<Link> _service = service;
    private readonly IEventDispatcher _dispatcher = dispatcher;
    private readonly IHashGenerator _hashGenerator = hashGenerator;
    private readonly ICustomAliasCreator _aliasCreator = aliasCreator;
    private readonly ILinkDeletionMarkerStore _deletionMarkers = deletionMarkers;
    private readonly ILogger<LinksController> _logger = logger;

    [HttpPost]
    public async Task<ActionResult<LinkResponse>> CreateLink(
        [FromBody] LinkCreateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Rejects empty/malformed input before it reaches the hash generator's own
        // ArgumentException.ThrowIfNullOrEmpty (which would otherwise surface as an opaque 500
        // via GlobalExceptionHandler instead of a 400), and - more importantly - rejects any
        // non-http(s) scheme so a stored link can never later redirect through RedirectApi to a
        // javascript:/data:/arbitrary-scheme target (an open redirect out of a trusted domain).
        if (!Uri.TryCreate(request.OriginalLink, UriKind.Absolute, out var originalUri) ||
            (originalUri.Scheme != Uri.UriSchemeHttp && originalUri.Scheme != Uri.UriSchemeHttps))
        {
            return Problem(
                detail: "OriginalLink must be an absolute http or https URL.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid link.");
        }

        if (request.TtlSeconds is <= 0)
        {
            return Problem(
                detail: "TtlSeconds must be a positive number of seconds.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid link.");
        }

        var createdAt = DateTime.UtcNow;
        // Frontend counts seconds, not a target timestamp - see LinkCreateRequest.TtlSeconds's own
        // comment for why. Computed once here so both creation paths (plain hash and custom alias)
        // apply the exact same rule.
        var expiresAt = request.TtlSeconds is { } ttlSeconds ? createdAt.AddSeconds(ttlSeconds) : (DateTime?)null;

        // Anonymous callers are fine — nothing here requires [Authorize]. If a valid Bearer token
        // is present, ASP.NET Core's auth middleware has already populated User from its claims;
        // if not (missing, expired, wrong signature), User just isn't authenticated and this is null.
        var userIdClaim = User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        var userId = Guid.TryParse(userIdClaim, out var parsedUserId) ? parsedUserId : (Guid?)null;

        if (request.Alias is not null)
        {
            return await CreateWithAliasAsync(request.Alias, request.OriginalLink, userId, createdAt, expiresAt, cancellationToken);
        }

        var hash = _hashGenerator.Generate(request.OriginalLink);

        var linkCreatedEvent = new LinkCreatedEvent
        {
            Hash = hash,
            OriginalLink = request.OriginalLink,
            ShortenLink = hash,
            CreatedAt = createdAt,
            UserId = userId,
            ExpiresAt = expiresAt,
        };

        // In the default (async/bus) messaging mode, this enqueues and returns in microseconds -
        // LocalPublishQueueWorker does the actual IMessagePublisher.PublishAsync call off this
        // request's critical path, so a slow or reconnecting RabbitMQ connection never shows up as
        // CreateLink latency. In gRPC/sync mode, this instead awaits ShortenerService persisting
        // the row before returning - see IEventDispatcher's own comment for why.
        await _dispatcher.DispatchAsync(linkCreatedEvent, Topics.LinkCreated, cancellationToken);

        // Optimistic, short-TTL cache entry - see OptimisticCacheTtlSeconds. Written only after
        // DispatchAsync succeeds, not before: in sync/gRPC mode a failed dispatch means the link was
        // never created at all, and this request fails outright (see GlobalExceptionHandler's
        // RpcException mapping) - writing the cache entry first would leave a ghost entry for a hash
        // that will never resolve anywhere, for up to a minute, for no reason.
        var link = new Link(hash, request.OriginalLink, hash, createdAt, userId, ExpiresAt: expiresAt);
        await _service.CacheAsync(link, hash, cancellationToken, OptimisticCacheTtlSeconds);

        // Debug, not Information - this fires on every CreateLink call, including under a k6 load
        // test hammering this endpoint at hundreds of req/s. Flip LinkApi to Debug locally when you
        // want to watch the create -> publish -> ShortenerService-persists flow step by step.
        _logger.LogDebug("Link {Hash} created for {OriginalLink}, queued for publish to {Topic}", hash, request.OriginalLink, Topics.LinkCreated);

        return new LinkResponse(hash, createdAt);
    }

    // Custom aliases take the synchronous gRPC path instead of the standing async/gRPC toggle
    // above, regardless of Messaging:Mode - see CustomAliasCreator and
    // sandbox/docs/scenarios/patterns/custom-aliases.md for why a real uniqueness guarantee needs a
    // synchronous round trip through ShortenerService (the record's owner) no matter what. Random
    // hashes stay async because a collision there is astronomically unlikely and unchecked (see the
    // same doc); a human-chosen alias colliding with another human's choice is not.
    private async Task<ActionResult<LinkResponse>> CreateWithAliasAsync(
        string rawAlias, string originalLink, Guid? userId, DateTime createdAt, DateTime? expiresAt, CancellationToken cancellationToken)
    {
        if (userId is null)
        {
            return Problem(
                detail: "Custom aliases require an authenticated user.",
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Authentication required.");
        }

        var alias = rawAlias.Trim().ToLowerInvariant();
        if (!AliasPattern().IsMatch(alias))
        {
            return Problem(
                detail: "Alias must be 3-32 characters, start with a letter or digit, and contain only lowercase letters, digits, '-' and '_'.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid alias.");
        }

        if (ReservedAliases.Contains(alias))
        {
            return Problem(
                detail: $"Alias '{alias}' is reserved.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid alias.");
        }

        var aliasEvent = new LinkCreatedEvent
        {
            Hash = alias,
            OriginalLink = originalLink,
            ShortenLink = alias,
            CreatedAt = createdAt,
            UserId = userId,
            HasCustomAlias = true,
            ExpiresAt = expiresAt,
        };

        var result = await _aliasCreator.CreateAsync(aliasEvent, cancellationToken);
        if (result == AliasCreationResult.AliasTaken)
        {
            return Problem(
                detail: $"Alias '{alias}' is already taken.",
                statusCode: StatusCodes.Status409Conflict,
                title: "Alias taken.");
        }

        // Unlike the optimistic, short-TTL cache entry in the plain-hash path above, this is a real
        // cache write with no explicit TTL override - by the time CreateAsync returns Created, the
        // row is already durably committed in Postgres (that's the whole point of the synchronous
        // path), so there's no "might never actually persist" risk to hedge against. Matches exactly
        // how LinkCreatedConsumer.cs itself populates the cache right after a real persist.
        var link = new Link(alias, originalLink, alias, createdAt, userId, HasCustomAlias: true, ExpiresAt: expiresAt);
        await _service.CacheAsync(link, alias, cancellationToken);

        _logger.LogDebug("Link {Hash} created via custom alias for {OriginalLink}", alias, originalLink);

        return new LinkResponse(alias, createdAt);
    }

    // Unlike CreateLink, this is a listing of links - anonymous callers have no "own links" to
    // list, so this requires either a valid Bearer token (scoped to that caller's own links) or the
    // InternalApiKey scheme (control-api's data-pool preload, which needs a broad sample across
    // every user's links, not one arbitrary user's own - a per-user JWT can't express that at all).
    [Authorize(AuthenticationSchemes = $"{JwtBearerDefaults.AuthenticationScheme},{Constants.InternalApiKeyAuthenticationScheme}")]
    [HttpGet]
    public async Task<ActionResult<LinksPageResponse>> GetLinks(
        [FromQuery] int page = 1,
        CancellationToken cancellationToken = default)
    {
        if (page < 1)
        {
            return Problem(
                detail: "page must be 1 or greater.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid query parameter.");
        }

        // Deleted/expired links are hidden by default - nothing in this feature calls for a
        // "show them anyway" toggle, so there isn't one; control-api's data-pool preload (the
        // InternalApiKey caller) gets the same filtering, since a deleted/expired row is no more
        // useful as sample data for it than it is in a user's own dashboard.
        var now = DateTime.UtcNow;
        var query = _context.Links.AsNoTracking()
            .Where(x => x.DeletedAt == null && (x.ExpiresAt == null || x.ExpiresAt > now));
        if (!User.HasClaim(Constants.InternalClaim, "true"))
        {
            // [Authorize] already guarantees one of the two schemes succeeded; for the JWT scheme
            // that means Sub is always present and parseable.
            var userId = Guid.Parse(User.FindFirstValue(JwtRegisteredClaimNames.Sub)!);
            query = query.Where(x => x.UserId == userId);
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var totalPages = (int)Math.Ceiling(totalCount / (double)PageSize);

        // (page - 1) * PageSize done in plain int arithmetic overflows to negative well before
        // int.MaxValue (~43M at PageSize=50) - Postgres then rejects the resulting OFFSET, turning a
        // page number past the end into an unhandled 500 instead of the empty result it should be.
        // Computing in long side-steps the overflow, and skipping the query entirely once the offset
        // is already past every row means Skip never sees a value large enough to overflow when cast
        // back to int, and avoids a wasted round trip for a page that can only come back empty.
        var skip = (long)(page - 1) * PageSize;
        var items = skip >= totalCount
            ? []
            : await query
                // CreatedAt alone isn't unique - two links created in the same request batch (or
                // restored/seeded rows) can share it, and SQL gives no ordering guarantee among rows
                // with an equal sort key, so a row could be duplicated across two pages or skipped
                // when paging through results. Hash (the primary key) as a tie-breaker makes the
                // order - and therefore which rows land on which page - fully deterministic.
                .OrderByDescending(x => x.CreatedAt)
                .ThenBy(x => x.Hash)
                .Skip((int)skip)
                .Take(PageSize)
                .Select(x => new LinkListItemResponse(x.ShortenLink, x.OriginalLink, x.CreatedAt, x.ClickCount, x.HasCustomAlias))
                .ToListAsync(cancellationToken);

        return new LinksPageResponse(items, page, PageSize, totalCount, totalPages);
    }

    [HttpGet("{hash}")]
    public async Task<ActionResult<LinkResponse>> GetLink(
        [FromRoute] string hash, CancellationToken cancellationToken)
    {
        async Task<Link?> Fetch()
        {
            var res = await _context.Links.FirstOrDefaultAsync(x => x.Hash == hash, cancellationToken);
            return res;
        }
        var res = await _service.GetOrFetch(hash, fetchFromDb: Fetch, cancellationToken);
        if (res is null)
        {
            return NotFound();
        }

        return new LinkResponse(res.ShortenLink, res.CreatedAt);
    }

    // Manual delete - the only trigger that needs the bus at all; the TTL sweep
    // (LinkExpiryWorker, ShortenerService) runs in the process that owns links_db and never comes
    // through here. Deliberately queried straight from Postgres, not the Link cache - this is a
    // rare, low-traffic request, not a hot path worth optimizing, and it needs an accurate
    // DeletedAt/UserId right now, not whatever the optimistic/short-TTL cache entry happens to say.
    [Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    [HttpDelete("{hash}")]
    public async Task<IActionResult> DeleteLink([FromRoute] string hash, CancellationToken cancellationToken)
    {
        var userId = Guid.Parse(User.FindFirstValue(JwtRegisteredClaimNames.Sub)!);

        var link = await _context.Links.AsNoTracking().FirstOrDefaultAsync(x => x.Hash == hash, cancellationToken);
        if (link is null)
        {
            return NotFound();
        }

        // 403, not 404 - the caller already knows this hash exists (they're asking to delete it);
        // telling the true owner's mistaken/malicious guess "not found" instead of "not allowed"
        // would leak nothing extra anyway, since the hash is already in their hands, but would be a
        // confusing response for a legitimate owner hitting this with the wrong account. Anonymous
        // links (UserId == null) always fail this check too - there is no owner who could ever pass.
        if (link.UserId != userId)
        {
            return Forbid();
        }

        if (link.DeletedAt is not null)
        {
            return NoContent(); // already deleted - idempotent success, not an error
        }

        // Dispatch first, mark the optimistic tombstone only after it succeeds - mirrors CreateLink's
        // own ordering above and for the same reason, mirrored in the opposite direction: a failed
        // dispatch (e.g. SyncGrpcDispatcher in gRPC mode, which has no fallback queue and throws
        // straight through to a 502/503 here) must not leave the tombstone behind regardless. Found
        // in review - the original order let a failed delete write a real 60s-TTL "Gone" tombstone
        // for a link that was never actually deleted, 410-ing every visitor for up to that long.
        await _dispatcher.DispatchAsync(new LinkDeletedEvent { Hash = hash }, Topics.LinkDeleted, cancellationToken);
        // Always published, regardless of whether this wins or loses the SETNX race below (a
        // concurrent duplicate DELETE for the same hash) - simpler than branching on the result,
        // and redundant delivery is exactly what LinkDeletedConsumer is built to tolerate.
        await _deletionMarkers.TryMarkOptimisticallyAsync(hash, DeletedReason.Manual, cancellationToken);

        _logger.LogDebug("Link {Hash} marked for deletion, queued for publish to {Topic}", hash, Topics.LinkDeleted);

        return NoContent();
    }
}
