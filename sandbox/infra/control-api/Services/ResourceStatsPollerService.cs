using ControlApi.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace ControlApi.Services;

// Every 2s, samples CPU/memory for every running service and pushes the latest batch to all
// connected clients. Separate from StatusPollerService (which watches container State) - this one
// hits the Docker stats endpoint per-container, which is heavier, so it runs on its own cadence.
public class ResourceStatsPollerService(
    IDockerService docker,
    ResourceStatsStore store,
    IHubContext<StatusHub> hub,
    ILogger<ResourceStatsPollerService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var containers = await docker.ListContainersAsync(stoppingToken);

                // Each Docker stats call is slow enough (observed ~2s each against Docker Desktop
                // on Windows) that doing this sequentially for ~10 containers made a full round
                // take 20+ seconds - defeating a 2s poll interval entirely. In parallel, a cycle
                // takes about as long as the single slowest call instead of the sum of all of them.
                var running = containers.Where(c => c.State == "running").ToList();
                var results = await Task.WhenAll(running.Select(c => docker.GetResourceSampleAsync(c.ServiceId, stoppingToken)));
                var samples = results.Where(s => s is not null).Select(s => s!).ToList();

                foreach (var sample in samples)
                {
                    store.Add(sample);
                }

                if (samples.Count > 0)
                {
                    await hub.Clients.All.SendAsync("resourceStatsUpdated", samples, stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Resource stats poll failed - will retry next tick");
            }
        }
    }
}
