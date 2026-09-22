using Common;
using Common.Models;
using Contracts.Events;
using Infrastructure;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace RedirectApi.Controllers;

[ApiController]
[Route("/")]
public class RedirectController(
    DatabaseContext context,
    IEntityCacheService<Link> cache,
    IMessagePublisher publisher,
    IClickCounterService clickCounter) : Controller
{
    private readonly DatabaseContext _context = context;
    private readonly IEntityCacheService<Link> _cache = cache;
    private readonly IMessagePublisher _publisher = publisher;
    private readonly IClickCounterService _clickCounter = clickCounter;

    [HttpGet("{hash}")]
    public async Task<IActionResult> RedirectToOrigin(
        [FromRoute] string hash,
        CancellationToken cancellationToken)
    {
        async Task<Link?> Fetch() =>
            await _context.Links.FirstOrDefaultAsync(x => x.Hash == hash, cancellationToken);

        var link = await _cache.GetOrFetch(hash, fetchFromDb: Fetch, cancellationToken);
        if (link is null)
        {
            return NotFound();
        }

        // Atomic, cheap, and doesn't touch Postgres - the counter shown on the "my links" page is
        // synced from Postgres separately, asynchronously, off the ClickTrackedEvent below.
        await _clickCounter.IncrementAsync(hash, cancellationToken);

        var clickEvent = new ClickTrackedEvent
        {
            Id = Guid.NewGuid(),
            Hash = hash,
            InboundLink = Request.GetDisplayUrl(),
            OutboundLink = link.OriginalLink,
            ClickedAt = DateTime.UtcNow,
            UserAgent = Request.Headers.UserAgent.ToString(),
            Referrer = Request.Headers.Referer.ToString(),
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
        };

        // Same pattern as LinkApi: published (or SQLite-fallback-queued) before responding, so a
        // click is never silently dropped just because the redirect finished first.
        await _publisher.PublishAsync(clickEvent, Topics.ClickTracked, cancellationToken);

        // 302 — a short link's target can change (or the link could be deleted), so this must
        // never be cached as permanent by the browser.
        return Redirect(link.OriginalLink);
    }
}
