using ControlApi.Hubs;
using ControlApi.Models;
using Microsoft.AspNetCore.SignalR;

namespace ControlApi.Services;

// Redis Sentinel and MongoDB's replica set can each re-elect their own leader with zero
// involvement from this app (see GetRedisTopologyAsync/GetMongoTopologyAsync) - polled here,
// server-side, and pushed to every connected client over the existing hub, the same pattern
// StatusPollerService already uses for container state. This replaces each browser tab polling the
// two topology endpoints itself: a failover now shows up as soon as control-api notices it (one
// poll interval, not one plus each tab's own), and control-api makes exactly one set of exec calls
// per tick no matter how many tabs are open.
public class TopologyPollerService(
    IDockerService docker,
    IHubContext<StatusHub> hub,
    ILogger<TopologyPollerService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private IReadOnlyList<NodeRole> _lastKnown = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var redis = await docker.GetRedisTopologyAsync(stoppingToken);
                var mongo = await docker.GetMongoTopologyAsync(stoppingToken);
                var current = redis.Roles.Concat(mongo.Roles).ToList();

                if (!HasChanged(_lastKnown, current))
                {
                    continue;
                }

                _lastKnown = current;
                await hub.Clients.All.SendAsync("infraTopologyUpdated", current, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Infra topology poll failed - will retry next tick");
            }
        }
    }

    internal static bool HasChanged(IReadOnlyList<NodeRole> previous, IReadOnlyList<NodeRole> current)
    {
        if (previous.Count != current.Count)
        {
            return true;
        }

        var previousByService = previous.ToDictionary(r => r.ServiceId);
        return current.Any(r => !previousByService.TryGetValue(r.ServiceId, out var prior) || prior.Role != r.Role);
    }
}
