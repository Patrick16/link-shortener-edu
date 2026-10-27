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
    IConfiguration configuration,
    ILogger<RedirectController> logger) : Controller
{
    private readonly DatabaseContext _context = context;
    private readonly IEntityCacheService<Link> _cache = cache;
    private readonly IEventDispatcher _dispatcher = dispatcher;
    private readonly IClickCounterService _clickCounter = clickCounter;
    private readonly ILinkDeletionMarkerStore _deletionMarkers = deletionMarkers;
    private readonly ILogger<RedirectController> _logger = logger;

    private const int DefaultRequestBudgetMs = 2000;

    // Not wired into docker-compose/the control panel the way Cache:Enabled etc. are - nothing
    // about "how patient should this hot path be" needs a live on/off A/B comparison the way those
    // toggles exist for. Configuration-overridable (RequestBudgetMs) purely so tests can shrink it
    // to a few milliseconds instead of a real production knob - see RedirectControllerTests for why
    // that matters for a budget whose whole mechanism is wall-clock elapsed time. Covers the
    // deletion-marker lookup plus the cache-or-DB fetch below (both normally sub-10ms; this only
    // ever bites when Redis and/or Postgres are genuinely degraded) - deliberately NOT covering the
    // click-tracking dispatch/counter further down, see the comment at that call site for why.
    private readonly TimeSpan _requestBudget = TimeSpan.FromMilliseconds(
        int.TryParse(configuration["RequestBudgetMs"], out var requestBudgetMs) ? requestBudgetMs : DefaultRequestBudgetMs);

    [HttpGet("{hash}")]
    public async Task<IActionResult> RedirectToOrigin(
        [FromRoute] string hash,
        CancellationToken cancellationToken)
    {
        Link? link;

        // Linked, not a bare CancelAfter on cancellationToken itself - this budget's timer must
        // never outlive the request (it's disposed via `using` below) and must never mask a genuine
        // client disconnect as a budget trip (the catch below tells the two apart by checking which
        // token actually fired).
        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budgetCts.CancelAfter(_requestBudget);
        var budgetToken = budgetCts.Token;

        try
        {
            // Checked first, before ever touching the main Link cache-aside path below - once a
            // hash is tombstoned, nothing else about the record matters for this decision, and this
            // skips both a Redis AND (on a cache miss) a Postgres round trip for a hash already
            // known dead.
            var marker = await _deletionMarkers.GetAsync(hash, budgetToken);
            if (marker is not null)
            {
                return GonePage(marker.Reason, marker.DeletedAt);
            }

            // Deliberately no explicit EF CommandTimeout here alongside the cancellation token -
            // EnableRetryOnFailure's execution strategy (see PostgresExtensions.cs) never retries an
            // OperationCanceledException, but a raw Npgsql command-timeout exception IS the kind of
            // transient-looking fault it's designed to retry, which would silently extend this
            // request well past the budget instead of shedding on time. The cancellation token
            // alone - no second, differently-behaved timeout mechanism layered on top - is what
            // keeps this budget an actual upper bound.
            async Task<Link?> Fetch() =>
                await _context.Links.AsNoTracking().FirstOrDefaultAsync(x => x.Hash == hash, budgetToken);

            link = await _cache.GetOrFetch(hash, fetchFromDb: Fetch, budgetToken);
        }
        catch (OperationCanceledException) when (budgetToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The budget fired, not the caller disconnecting (that case's own IsCancellationRequested
            // would already be true on cancellationToken too, and is deliberately left to propagate
            // instead of being caught here - there's no client left to send a 503 to, and the
            // framework's own handling of a cancelled request is the right one for that case).
            return BudgetExceededResult();
        }

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

        // cancellationToken here, deliberately NOT budgetToken (already out of scope by this point
        // anyway - the budget only ever wraps the lookup above) - the click has already been
        // resolved to a destination, so dispatch/the counter below are the "must happen regardless"
        // side effect PLAN.md's own framing for this item calls out: in async/bus mode this is a
        // near-instant channel write with no fallback of its own if cancelled mid-write (see
        // LocalPublishQueue.EnqueueAsync), and in gRPC/sync mode cancelling client-side here can't
        // undo a downstream write TrafficService may already be persisting. A deadline that could
        // cut either short would turn "shed an overloaded request" into "silently drop a click" -
        // exactly the failure mode this item's own pitfall note warns about. Left unbounded by a
        // deadline here, not by an oversight: the circuit breaker already covers "downstream
        // persistently broken" for the gRPC path (BrokenCircuitException -> 503, see
        // SyncGrpcDispatcher), and the async path's own SQLite fallback/retry worker already covers
        // "RabbitMQ unreachable" - the budget's job is specifically the lookup phase above, which
        // had no equivalent gate before this item.
        //
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

    // ProblemDetails, not HtmlPage - unlike Gone/NotFound above, this isn't a human-readable
    // end-state a browser navigates to, it's a transient "ask again shortly" signal a load-testing
    // tool or a retrying client is the actual audience for, same shape as every other error response
    // in this API (see GlobalExceptionHandler). Retry-After: 1 is a short, fixed suggestion, not a
    // computed backoff - this budget is about shedding THIS request quickly, not about teaching
    // clients a real retry schedule for an overloaded dependency.
    private ObjectResult BudgetExceededResult()
    {
        Response.Headers["Retry-After"] = "1";
        return Problem(
            detail: $"This request's {_requestBudget.TotalMilliseconds:0}ms budget was exceeded while resolving the link - shed instead of waiting on a slow dependency.",
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Request budget exceeded.");
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
