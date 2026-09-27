namespace ControlApi.Services;

// Periodically checks that every Sentinel's own remembered master address still actually points at
// whichever Redis container is really the master right now, and corrects it if not - see
// DockerService.SelfHealSentinelAsync's own comment for the incident (Sentinel found monitoring an
// unrelated container's reused IP) that motivated this. 30s, not TopologyPollerService's 2s: this
// does real exec work even when nothing's wrong (3 ROLE reads + 3 SENTINEL reads every tick) and
// only ever needs to catch drift, not report live state to the UI - there's no user-facing signal
// this feeds, so there's no reason to run it as often as the poller that does.
public class SentinelSelfHealPollerService(
    IDockerService docker,
    ILogger<SentinelSelfHealPollerService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await docker.SelfHealSentinelAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Sentinel self-heal check failed - will retry next tick");
            }
        }
    }
}
