using System.Text.Json;
using Infrastructure;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace ReportingApi.Realtime;

// Listens for click notifications from RedirectApi and pushes each affected, watched user's top list
// to their open dashboards. Two limits keep this from becoming a query per click: the scheduler caps
// it at one push per user per MinPushInterval, and a push is skipped entirely if the list came out
// identical to the last one sent.
//
// Every ReportingApi replica runs its own copy of this service, each independently subscribing to
// the SAME Redis pub/sub channel (SubscribeWithRetryAsync) and independently tracking its own
// LOCAL connections (TopLinksSubscriptions/TopLinksPushScheduler are per-process). That's fine for
// a user whose every connection lands on the same replica (ip_hash's common case - see nginx.conf),
// since the replica that decides to push is also the only one with a local connection to deliver
// to. It stops being fine now that the SignalR Redis backplane is wired (backlog item 11): a user
// connected from two DIFFERENT replicas (two devices on different networks, the exact case the
// backplane exists for) gets double-pushed without PushAsync's own claim below - both replicas see
// the same notification, both see the user as locally watched, both independently query ClickHouse
// and call Clients.Group(...), and the backplane fans out EACH of those to EVERY connection in the
// group (found in review - see .notes/review-reports for this item).
public sealed class TopLinksPushService(
    IConnectionMultiplexer connectionMultiplexer,
    TopLinksPushScheduler scheduler,
    TopLinksSubscriptions subscriptions,
    ITopLinksReader reader,
    IHubContext<TopLinksHub> hub,
    ILogger<TopLinksPushService> logger) : BackgroundService
{
    private static readonly TimeSpan MinPushInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMilliseconds(500);

    private readonly IConnectionMultiplexer _connectionMultiplexer = connectionMultiplexer;
    private readonly TopLinksPushScheduler _scheduler = scheduler;
    private readonly TopLinksSubscriptions _subscriptions = subscriptions;
    private readonly ITopLinksReader _reader = reader;
    private readonly IHubContext<TopLinksHub> _hub = hub;
    private readonly ILogger<TopLinksPushService> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SubscribeWithRetryAsync(stoppingToken);

        using var timer = new PeriodicTimer(CheckInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            foreach (var userId in _scheduler.TakeDue(DateTime.UtcNow, MinPushInterval))
            {
                if (!_subscriptions.IsWatched(userId))
                {
                    _scheduler.Forget(userId);
                    continue;
                }

                await PushAsync(userId, stoppingToken);
            }
        }
    }

    // Only the first subscribe needs this: once established, StackExchange.Redis re-subscribes on
    // its own after a reconnect. Retrying here keeps a Redis outage at boot from stopping the host,
    // which would also take down the ClickHouse report endpoints this service serves.
    private async Task SubscribeWithRetryAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (true)
        {
            try
            {
                await _connectionMultiplexer.GetSubscriber().SubscribeAsync(
                    RedisChannel.Literal(TopLinks.ChangedChannel),
                    (_, message) =>
                    {
                        // Only watched users need a push. Marking every owner dirty would leave
                        // throttle state behind for each one, since most owners never have a dashboard open.
                        if (Guid.TryParseExact(message.ToString(), "N", out var userId) && _subscriptions.IsWatched(userId))
                        {
                            _scheduler.MarkDirty(userId);
                        }
                    });
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to subscribe to {Channel}, retrying in {Delay}", TopLinks.ChangedChannel, delay);
                await Task.Delay(delay, stoppingToken);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, TimeSpan.FromSeconds(30).Ticks));
            }
        }
    }

    internal async Task PushAsync(Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            // Cluster-wide claim, not just this replica's own IsUnchanged/MinPushInterval throttle
            // (see this class's own header comment for why that stopped being enough once the
            // backplane was wired). SET NX PX, same atomic-claim idiom
            // RedisLinkDeletionMarkerStore already uses elsewhere in this codebase - whichever
            // replica wins proceeds; the TTL (MinPushInterval, the same number this push would
            // have been throttled to locally anyway) is what lets a later, genuinely new change
            // still get through once it expires, rather than permanently locking a user to one
            // replica's pushes.
            if (!await TryClaimPushAsync(userId, cancellationToken))
            {
                return;
            }

            var top = await _reader.GetTopAsync(userId, DateTime.UtcNow, cancellationToken);
            var payload = JsonSerializer.Serialize(top);
            if (_scheduler.IsUnchanged(userId, payload))
            {
                return;
            }

            _scheduler.RecordPayload(userId, payload);

            // Re-checked after recording: a disconnect that landed during the query above would
            // otherwise have its Forget() run before RecordPayload re-added this user's payload.
            // Recording first and checking second closes that window, since a disconnect always
            // updates the subscription registry before it calls Forget().
            if (!_subscriptions.IsWatched(userId))
            {
                _scheduler.Forget(userId);
                return;
            }

            await _hub.Clients.Group(TopLinksHub.GroupFor(userId))
                .SendAsync(TopLinksHub.TopLinksMethod, top, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One user's failed push shouldn't stop the loop from serving everyone else.
            _logger.LogWarning(ex, "Failed to push top links to user {UserId}", userId);
        }
    }

    // Fail-OPEN, not closed: if Redis itself is unreachable for the claim call, every replica
    // proceeding independently (this service's pre-backplane behavior) is still strictly better
    // than every replica skipping every push - a transient Redis failure here must degrade to "a
    // dashboard might see one duplicate update" territory, never to "no replica pushes anything
    // at all" territory.
    private async Task<bool> TryClaimPushAsync(Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            var db = _connectionMultiplexer.GetDatabase();
            return await db.StringSetAsync(PushClaimKey(userId), "1", MinPushInterval, When.NotExists);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to claim push for user {UserId} - proceeding without cluster-wide dedup for this tick", userId);
            return true;
        }
    }

    private static string PushClaimKey(Guid userId) => $"clicks:user:{userId:N}:push-claim";
}
