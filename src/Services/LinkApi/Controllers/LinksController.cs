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
    private const string LinkCreatedTopic = "link.created";

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

        var linkCreatedEvent = new LinkCreatedEvent
        {
            Hash = hash,
            OriginalLink = request.OriginalLink,
            ShortenLink = hash,
            CreatedAt = createdAt,
            UserId = null
        };

        await _publisher.PublishAsync(linkCreatedEvent, LinkCreatedTopic, cancellationToken);

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
