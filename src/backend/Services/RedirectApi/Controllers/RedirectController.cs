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
    ILinkDeletionMarkerStore deletionMarkers,
    ILogger<RedirectController> logger) : Controller
{
    private readonly DatabaseContext _context = context;
    private readonly IEntityCacheService<Link> _cache = cache;
    private readonly IEventDispatcher _dispatcher = dispatcher;
    private readonly IClickCounterService _clickCounter = clickCounter;
    private readonly ILinkDeletionMarkerStore _deletionMarkers = deletionMarkers;
    private readonly ILogger<RedirectController> _logger = logger;

    [HttpGet("{hash}")]
    public async Task<IActionResult> RedirectToOrigin(
        [FromRoute] string hash,
        CancellationToken cancellationToken)
    {
        // Checked first, before ever touching the main Link cache-aside path below - once a hash
        // is tombstoned, nothing else about the record matters for this decision, and this skips
        // both a Redis AND (on a cache miss) a Postgres round trip for a hash already known dead.
        var marker = await _deletionMarkers.GetAsync(hash, cancellationToken);
        if (marker is not null)
        {
            return GonePage(marker.Reason, marker.DeletedAt);
        }

        async Task<Link?> Fetch() =>
            await _context.Links.AsNoTracking().FirstOrDefaultAsync(x => x.Hash == hash, cancellationToken);

        var link = await _cache.GetOrFetch(hash, fetchFromDb: Fetch, cancellationToken);
        if (link is null)
        {
            // Debug, not Warning - an unknown hash is an everyday user/bot mistake (typo, expired
            // link, scanner probing), not a system anomaly worth surfacing by default.
            _logger.LogDebug("Redirect miss for unknown hash {Hash}", hash);
            return NotFoundPage();
        }

        // Live check, not just the tombstone lookup above: the tombstone is only guaranteed to
        // exist once LinkDeletedConsumer/LinkExpiryWorker has actually confirmed it, and the TTL
        // sweep runs once a minute - without this, a link could sit expired-but-still-redirecting
        // for up to that whole window. DeletedAt is checked too in case the confirm step's own
        // Redis write failed (RedisLinkDeletionMarkerStore fails open) but the Postgres row itself
        // is already the real, authoritative answer either way.
        if (link.DeletedAt is not null || (link.ExpiresAt is { } expiresAt && expiresAt <= DateTime.UtcNow))
        {
            var reason = link.DeletedAt is not null ? DeletedReason.Manual : DeletedReason.Expired;
            return GonePage(reason, link.DeletedAt ?? link.ExpiresAt!.Value);
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
            // Feeds ReportingService's owner-scoped reporting (ClickFact.UserId) - the same value
            // already passed to _clickCounter.RecordClickAsync below for Live Top Links.
            UserId = link.UserId,
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

        // Recorded only after the dispatch above succeeds - doing this unconditionally before the
        // dispatch (the original order) left a bucket incremented for a click that, in gRPC mode,
        // could still fail downstream and never get redirected or persisted anywhere (found during
        // review: SyncGrpcDispatcher has no fallback queue, so a failed dispatch here throws and the
        // whole request 502s/503s). Anonymous links are a no-op inside the counter.
        await _clickCounter.RecordClickAsync(hash, link.UserId, clickEvent.ClickedAt, cancellationToken);

        // Debug, not Information - this is the hottest path in the whole system under a load test
        // (every single click). Flip RedirectApi to Debug locally to watch redirect -> publish ->
        // TrafficService/ShortenerService-consume step by step.
        _logger.LogDebug("Redirecting {Hash} -> {OriginalLink}, click {ClickId} queued for publish", hash, link.OriginalLink, clickEvent.Id);

        // 302 — a short link's target can change (or the link could be deleted), so this must
        // never be cached as permanent by the browser.
        return Redirect(link.OriginalLink);
    }

    // HTML, not ProblemDetails - the first genuinely HTML response anywhere in src/backend. A
    // short-link redirect is followed directly by a browser's top-level navigation, not a script
    // reading JSON, so a human needs something readable here instead of this API's usual
    // {type,title,status,detail} shape. Written directly to the response body via ContentResult
    // (not a bare NotFound()/StatusCode()) specifically so UseApiExceptionHandling's
    // UseStatusCodePages callback - which only fires on a response that's still empty - leaves it
    // alone; verified live, not just assumed from the ASP.NET Core docs (see this feature's plan).
    private static ContentResult GonePage(DeletedReason reason, DateTime deletedAt) =>
        HtmlPage(
            StatusCodes.Status410Gone,
            "Link gone",
            reason == DeletedReason.Expired
                ? $"This link expired on {deletedAt:u} and is no longer available."
                : $"This link was deleted on {deletedAt:u} and is no longer available.");

    private static ContentResult NotFoundPage() =>
        HtmlPage(StatusCodes.Status404NotFound, "Link not found", "This link doesn't exist.");

    private static ContentResult HtmlPage(int statusCode, string title, string message) => new()
    {
        StatusCode = statusCode,
        ContentType = "text/html; charset=utf-8",
        Content = $"""
            <!DOCTYPE html>
            <html lang="en">
            <head><meta charset="utf-8"><title>{title}</title></head>
            <body style="font-family: sans-serif; text-align: center; margin-top: 15vh;">
                <h1>{title}</h1>
                <p>{message}</p>
            </body>
            </html>
            """,
    };
}
