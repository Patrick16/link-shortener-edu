using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
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
public class LinksController(
    DatabaseContext context,
    IEntityCacheService<Link> service,
    IMessagePublisher publisher,
    IHashGenerator hashGenerator) : Controller
{
    private const int PageSize = 50;

    private readonly DatabaseContext _context = context;
    private readonly IEntityCacheService<Link> _service = service;
    private readonly IMessagePublisher _publisher = publisher;
    private readonly IHashGenerator _hashGenerator = hashGenerator;

    [HttpPost]
    public async Task<ActionResult<LinkResponse>> CreateLink(
        [FromBody] LinkCreateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var hash = _hashGenerator.Generate(request.OriginalLink);
        var createdAt = DateTime.UtcNow;

        // Anonymous callers are fine — nothing here requires [Authorize]. If a valid Bearer token
        // is present, ASP.NET Core's auth middleware has already populated User from its claims;
        // if not (missing, expired, wrong signature), User just isn't authenticated and this is null.
        var userIdClaim = User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        var userId = Guid.TryParse(userIdClaim, out var parsedUserId) ? parsedUserId : (Guid?)null;

        var linkCreatedEvent = new LinkCreatedEvent
        {
            Hash = hash,
            OriginalLink = request.OriginalLink,
            ShortenLink = hash,
            CreatedAt = createdAt,
            UserId = userId
        };

        await _publisher.PublishAsync(linkCreatedEvent, Topics.LinkCreated, cancellationToken);

        return new LinkResponse(hash, createdAt);
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

        var query = _context.Links.AsNoTracking();
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
                .Select(x => new LinkListItemResponse(x.ShortenLink, x.OriginalLink, x.CreatedAt, x.ClickCount))
                .ToListAsync(cancellationToken);

        return new LinksPageResponse(items, page, PageSize, totalCount, totalPages);
    }

    [HttpGet("{hash}")]
    public async Task<ActionResult<LinkResponse>> GetLink(
        [FromRoute] string hash,CancellationToken cancellationToken)
    {
        async Task<Link?> Fetch()
        {
            var res = await _context.Links.FirstOrDefaultAsync(x => x.Hash == hash, cancellationToken);
            return res;
        }
        var res = await _service.GetOrFetch(hash, fetchFromDb: Fetch, cancellationToken);
        if(res is null)
        {
            return NotFound();
        }

        return new LinkResponse(res.ShortenLink, res.CreatedAt);
    }
}
