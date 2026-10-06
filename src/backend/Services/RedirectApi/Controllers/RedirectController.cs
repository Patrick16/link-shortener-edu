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
    IEventDispatcher dispatcher,
    IClickCounterService clickCounter,
    ILogger<RedirectController> logger) : Controller
{
    private readonly DatabaseContext _context = context;
    private readonly IEntityCacheService<Link> _cache = cache;
    private readonly IEventDispatcher _dispatcher = dispatcher;
    private readonly IClickCounterService _clickCounter = clickCounter;
    private readonly ILogger<RedirectController> _logger = logger;

    [HttpGet("{hash}")]
    public async Task<IActionResult> RedirectToOrigin(
        [FromRoute] string hash,
        CancellationToken cancellationToken)
    {
        async Task<Link?> Fetch() =>
            await _context.Links.AsNoTracking().FirstOrDefaultAsync(x => x.Hash == hash, cancellationToken);

        var link = await _cache.GetOrFetch(hash, fetchFromDb: Fetch, cancellationToken);
        if (link is null)
        {
            // Debug, not Warning - an unknown hash is an everyday user/bot mistake (typo, expired
            // link, scanner probing), not a system anomaly worth surfacing by default.
            _logger.LogDebug("Redirect miss for unknown hash {Hash}", hash);
            return NotFound();
        }

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

        // In the default (async/bus) messaging mode, this enqueues and returns in microseconds -
        // LocalPublishQueueWorker does the actual IMessagePublisher.PublishAsync call (RabbitMQ, or
        // its SQLite fallback) off this request's critical path, so a slow or reconnecting RabbitMQ
        // connection never delays the redirect itself (see sandbox/docs/pgcat-pool-sizing.md,
        // Symptom 7, for the 57.7s outlier this used to cause when a publish was awaited here
        // directly). In gRPC/sync mode, this instead awaits TrafficService persisting the click
        // before the redirect itself is returned - the hot path for every single click becomes
        // coupled to a downstream analytics write's availability. That coupling cost, made visible
        // on the busiest path in the whole system, is the entire point of this messaging mode.
        //
        // Also in gRPC mode: the RabbitMQ topic exchange's other two fan-out consumers of
        // click.tracked (ShortenerService's click-count bump, ReportingService's ClickHouse write)
        // never receive this event at all, since nothing is ever published to RabbitMQ in this
        // mode - SyncGrpcDispatcher calls TrafficService directly and stops there. The "my links"
        // dashboard's click count and the ClickHouse-backed reports both silently go stale while
        // gRPC mode is active; TrafficService's own Postgres/Mongo writes are unaffected. Known,
        // deliberate asymmetry of point-to-point RPC vs. pub/sub fan-out for this v1 (found during
        // review - previously only the ReportingService half of this was documented).
        await _dispatcher.DispatchAsync(clickEvent, Topics.ClickTracked, cancellationToken);

        // Incremented only after the dispatch above succeeds - doing this unconditionally before
        // the dispatch (the original order) left the counter incremented for a click that, in
        // gRPC mode, could still fail downstream and never get redirected or persisted anywhere
        // (found during review: SyncGrpcDispatcher has no fallback queue, so a failed dispatch here
        // throws and the whole request 502s/503s). Still cheap/atomic and still doesn't touch
        // Postgres - the exact count shown on the "my links" page is synced from Postgres
        // separately, asynchronously, off the ClickTrackedEvent above.
        await _clickCounter.IncrementAsync(hash, cancellationToken);

        // Debug, not Information - this is the hottest path in the whole system under a load test
        // (every single click). Flip RedirectApi to Debug locally to watch redirect -> publish ->
        // TrafficService/ShortenerService-consume step by step.
        _logger.LogDebug("Redirecting {Hash} -> {OriginalLink}, click {ClickId} queued for publish", hash, link.OriginalLink, clickEvent.Id);

        // 302 — a short link's target can change (or the link could be deleted), so this must
        // never be cached as permanent by the browser.
        return Redirect(link.OriginalLink);
    }
}
