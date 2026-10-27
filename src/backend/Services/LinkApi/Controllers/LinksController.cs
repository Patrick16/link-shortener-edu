using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    IIdempotencyStore idempotency,
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

    // Every other client-supplied identifier here is bounded (alias: 3-32 chars); an
    // Idempotency-Key had no cap at all, so a buggy/abusive client could mint an unbounded number
    // of large Redis keys, each held for the full 24h completed-TTL (found during review). 256
    // matches the conventional bound other idempotency-key APIs (e.g. Stripe) use for the same
    // opaque-token header.
    private const int MaxIdempotencyKeyLength = 256;

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
    private readonly IIdempotencyStore _idempotency = idempotency;
    private readonly ILogger<LinksController> _logger = logger;

    // CamelCase explicitly, not just left to System.Text.Json's PascalCase-preserving default -
    // ASP.NET Core's own MVC JSON formatter serializes every other response in this API as
    // camelCase (its own built-in default), so a byte-for-byte faithful replay has to match that
    // convention deliberately, not inherit whatever a bare JsonSerializerOptions happens to do.
    private static readonly JsonSerializerOptions ReplayJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [HttpPost]
    public async Task<ActionResult<LinkResponse>> CreateLink(
        [FromBody] LinkCreateRequest request,
        CancellationToken cancellationToken,
        // Optional, header-based (same placement convention real idempotency-key APIs use) - no
        // key means the pre-existing at-least-once behavior (a client retry after a timeout can
        // still mint a second link). Kept after cancellationToken, not before, same reason GetLinks'
        // own `deleted` param is - existing positional callers (every test in this file) keep
        // compiling unchanged.
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!string.IsNullOrWhiteSpace(idempotencyKey))
        {
            if (idempotencyKey.Length > MaxIdempotencyKeyLength)
            {
                return Problem(
                    detail: $"Idempotency-Key must be at most {MaxIdempotencyKeyLength} characters.",
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Invalid idempotency key.");
            }

            return await CreateLinkIdempotentlyAsync(idempotencyKey, request, cancellationToken);
        }

        return await CreateLinkCoreAsync(request, cancellationToken);
    }

    // Wraps CreateLinkCoreAsync (unchanged below) in a claim/replay cycle instead of threading the
    // idempotency key through every branch of it - see IIdempotencyStore's own comment for the
    // claim/complete/evict shape this drives. Every outcome CreateLinkCoreAsync can produce (the
    // success path, or any Problem() - 400/401/409) is treated as a legitimately replayable,
    // deterministic result for the same key; only an actual unhandled exception evicts the claim,
    // so a genuine 500 doesn't poison the key for a legitimate retry.
    private async Task<ActionResult<LinkResponse>> CreateLinkIdempotentlyAsync(
        string idempotencyKey, LinkCreateRequest request, CancellationToken cancellationToken)
    {
        if (!await _idempotency.TryClaimAsync(idempotencyKey, cancellationToken))
        {
            var existing = await _idempotency.GetAsync(idempotencyKey, cancellationToken);
            if (existing is null)
            {
                // Lost the race to claim, then the record vanished before this could read it (a
                // Redis blip between the two calls, or the winner's claim already expired) -
                // fail open rather than block the caller on a transient read gap.
                return await CreateLinkCoreAsync(request, cancellationToken);
            }

            if (existing.Status == IdempotencyStatus.Pending)
            {
                return Problem(
                    detail: "A request with this Idempotency-Key is already being processed - retry shortly.",
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Request in progress.");
            }

            // Replay the first caller's exact prior outcome - no new hash generated, nothing
            // re-published to the bus.
            return BuildReplayResult(existing);
        }

        try
        {
            var result = await CreateLinkCoreAsync(request, cancellationToken);
            var (statusCode, contentType, bodyJson) = ExtractForReplay(result);
            await _idempotency.CompleteAsync(idempotencyKey, statusCode, contentType, bodyJson, cancellationToken);
            return result;
        }
        catch
        {
            await _idempotency.EvictAsync(idempotencyKey, cancellationToken);
            throw;
        }
    }

    // A replayed problem+json body must not carry the ORIGINAL caller's traceId - every
    // ProblemDetails response gets one baked in at Problem()-call time (ApiExceptionHandling
    // Extensions' CustomizeProblemDetails), so a byte-for-byte replay of the stored JSON would
    // otherwise serve a stale, unrelated trace id in the one field meant to correlate this
    // response with the request that produced it (found during review). Re-stamped with THIS
    // request's own HttpContext.TraceIdentifier instead. JsonNode, not the immutable JsonElement
    // ReplayJsonOptions path below uses elsewhere, specifically because it needs to be mutated in
    // place; the success-response shape (LinkResponse) has no traceId field at all and is
    // unaffected.
    private ObjectResult BuildReplayResult(IdempotencyRecord existing)
    {
        if (existing.ContentType == "application/problem+json")
        {
            var node = JsonNode.Parse(existing.BodyJson!)!;
            node["traceId"] = HttpContext.TraceIdentifier;
            return new ObjectResult(node) { StatusCode = existing.StatusCode, ContentTypes = { existing.ContentType! } };
        }

        return new ObjectResult(JsonSerializer.Deserialize<JsonElement>(existing.BodyJson!, ReplayJsonOptions))
        {
            StatusCode = existing.StatusCode,
            ContentTypes = { existing.ContentType! },
        };
    }

    // ActionResult<T>'s own duality (a bare value via implicit conversion vs. an explicit
    // ObjectResult from Problem()) means there's no single property that always holds "the status
    // code and body" - this normalizes both shapes into the (status, content-type, JSON) triple
    // CompleteAsync persists and a replay reconstructs from.
    private static (int StatusCode, string ContentType, string BodyJson) ExtractForReplay(ActionResult<LinkResponse> result)
    {
        if (result.Result is ObjectResult objectResult)
        {
            var statusCode = objectResult.StatusCode ?? StatusCodes.Status200OK;
            // Problem()'s ObjectResult doesn't reliably carry "application/problem+json" in
            // ContentTypes itself (that's usually applied by output-formatter content negotiation
            // at execution time, which never runs here) - inferred from the CLR type instead.
            var contentType = objectResult.Value is ProblemDetails ? "application/problem+json" : "application/json";
            return (statusCode, contentType, JsonSerializer.Serialize(objectResult.Value, ReplayJsonOptions));
        }

        // The success path returns a bare LinkResponse (implicit ActionResult<T> conversion) -
        // ASP.NET Core serializes that as 200 OK, application/json.
        return (StatusCodes.Status200OK, "application/json", JsonSerializer.Serialize(result.Value, ReplayJsonOptions));
    }

    private async Task<ActionResult<LinkResponse>> CreateLinkCoreAsync(
        LinkCreateRequest request,
        CancellationToken cancellationToken)
    {
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
        CancellationToken cancellationToken = default,
        // Two disjoint views, not a three-way "all/active/deleted" toggle - a deleted/expired link
        // is terminal (its hash is never reissued), so there's nothing a caller needs from seeing
        // both at once. Defaults to the active view - every existing caller (control-api's
        // data-pool preload included) keeps today's behavior unless it explicitly opts in. Kept
        // after `cancellationToken`, not before, so existing positional callers (every test in
        // LinksControllerTests.cs) keep compiling unchanged.
        [FromQuery] bool deleted = false)
    {
        if (page < 1)
        {
            return Problem(
                detail: "page must be 1 or greater.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid query parameter.");
        }

        var now = DateTime.UtcNow;
        var query = _context.Links.AsNoTracking().Where(x =>
            deleted
                ? x.DeletedAt != null || (x.ExpiresAt != null && x.ExpiresAt <= now)
                : x.DeletedAt == null && (x.ExpiresAt == null || x.ExpiresAt > now));
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
        // CreatedAt/DeletedAt alone aren't unique - rows can share either, and SQL gives no ordering
        // guarantee among rows with an equal sort key, so a row could be duplicated across two pages
        // or skipped when paging through results. Hash (the primary key) as a tie-breaker makes the
        // order - and therefore which rows land on which page - fully deterministic. The deleted view
        // sorts by DeletedAt (when it actually left the active list) rather than CreatedAt (when it
        // was made) - the more relevant "recency" for a list whose whole point is "what just
        // disappeared". Two separate OrderByDescending calls, not a ternary inside one, to keep the
        // sort key's nullability (DeletedAt) from ever mixing with CreatedAt's in one expression.
        var ordered = deleted
            ? query.OrderByDescending(x => x.DeletedAt).ThenBy(x => x.Hash)
            : query.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Hash);

        var skip = (long)(page - 1) * PageSize;
        var items = skip >= totalCount
            ? []
            : await ordered
                .Skip((int)skip)
                .Take(PageSize)
                .Select(x => new LinkListItemResponse(x.ShortenLink, x.OriginalLink, x.CreatedAt, x.ClickCount, x.HasCustomAlias, x.ExpiresAt, x.DeletedAt))
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

        // Mirrors RedirectApi's own live check (RedirectController.cs) - a deleted/expired link's
        // row (or its optimistic/cached copy) can still be present until the deletion consumer or
        // the TTL sweep catches up, but it must not be readable as if it were still active. Found
        // in review: without this, GetLinks (which does filter DeletedAt/ExpiresAt) and GetLink
        // disagreed about whether a gone link's metadata was still visible.
        if (res.DeletedAt is not null || (res.ExpiresAt is { } expiresAt && expiresAt <= DateTime.UtcNow))
        {
            return Problem(
                detail: "This link has expired or been deleted.",
                statusCode: StatusCodes.Status410Gone,
                title: "Link gone.");
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
