using ControlApi.Hubs;
using ControlApi.Models;
using Microsoft.AspNetCore.SignalR;

namespace ControlApi.Services;

// Polls docker for container status and pushes to every connected client whenever it changes -
// no Docker events API subscription (would be more efficient) for this first cut, a 1.5s poll is
// simple and fast enough for a panel a human is watching.
public class StatusPollerService(
    IDockerService docker,
    IHubContext<StatusHub> hub,
    ILogger<StatusPollerService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1.5);

    private IReadOnlyList<ManagedContainer> _lastKnown = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var current = await docker.ListContainersAsync(stoppingToken);
                if (!HasChanged(_lastKnown, current))
                {
                    continue;
                }

                _lastKnown = current;
                await hub.Clients.All.SendAsync("containersUpdated", current, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Status poll failed - will retry next tick");
            }
        }
    }

    internal static bool HasChanged(IReadOnlyList<ManagedContainer> previous, IReadOnlyList<ManagedContainer> current)
    {
        if (previous.Count != current.Count)
        {
            return true;
        }

        // Compares State (running/exited/paused/...) only, not the human-readable Status text -
        // Docker's Status string embeds an elapsed-time clock ("Up 3 seconds" -> "Up 4 seconds")
        // that ticks on every poll, which would otherwise push on every single cycle forever.
        // Keyed by ContainerId, not ServiceId - a scaled service has multiple containers sharing
        // one ServiceId, which would collide as duplicate dictionary keys.
        var previousByContainer = previous.ToDictionary(c => c.ContainerId);
        return current.Any(c => !previousByContainer.TryGetValue(c.ContainerId, out var prior)
            || prior.State != c.State);
    }
}
