using System.IdentityModel.Tokens.Jwt;
using Common;
using Common.Models;
using Contracts.Events;
using Infrastructure;
using LinkApi.Models;
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
