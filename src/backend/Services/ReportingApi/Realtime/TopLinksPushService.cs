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
        await _connectionMultiplexer.GetSubscriber().SubscribeAsync(
            RedisChannel.Literal(TopLinks.ChangedChannel),
            (_, message) =>
            {
                if (Guid.TryParseExact(message.ToString(), "N", out var userId))
                {
                    _scheduler.MarkDirty(userId);
                }
            });

        using var timer = new PeriodicTimer(CheckInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            foreach (var userId in _scheduler.TakeDue(DateTime.UtcNow, MinPushInterval))
            {
                if (!_subscriptions.IsWatched(userId))
                {
                    continue;
                }

                await PushAsync(userId, stoppingToken);
            }
        }
    }

    private async Task PushAsync(Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            var top = await _reader.GetTopAsync(userId, DateTime.UtcNow, cancellationToken);
            var payload = JsonSerializer.Serialize(top);
            if (_scheduler.IsUnchanged(userId, payload))
            {
                return;
            }

            _scheduler.RecordPayload(userId, payload);
            await _hub.Clients.Group(TopLinksHub.GroupFor(userId))
                .SendAsync(TopLinksHub.TopLinksMethod, top, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One user's failed push shouldn't stop the loop from serving everyone else.
            _logger.LogWarning(ex, "Failed to push top links to user {UserId}", userId);
        }
    }
}
