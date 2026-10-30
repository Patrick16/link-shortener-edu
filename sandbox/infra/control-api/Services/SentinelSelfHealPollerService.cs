namespace ControlApi.Services;

// Periodically checks that every Sentinel's own remembered master address still actually points at
// whichever Redis container is really the master right now, and corrects it if not - see
// RedisInfraService.SelfHealSentinelAsync's own comment for the incident (Sentinel found monitoring
// an unrelated container's reused IP) that motivated this. 30s, not TopologyPollerService's 2s: this
// does real exec work even when nothing's wrong (3 ROLE reads + 3 SENTINEL reads every tick) and
// only ever needs to catch drift, not report live state to the UI - there's no user-facing signal
// this feeds, so there's no reason to run it as often as the poller that does.
public class SentinelSelfHealPollerService(
    IRedisInfraService redis,
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
                await redis.SelfHealSentinelAsync(stoppingToken);
                // Same tick, not a separate poller - both correct the same underlying drift (a
                // stale learned address after a redis-master recreate Sentinel never saw as a
                // failure), just on different sides (Sentinels' belief vs. a replica's own
                // REPLICAOF) - see SelfHealReplicasAsync's own comment.
                await redis.SelfHealReplicasAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Sentinel/replica self-heal check failed - will retry next tick");
            }
        }
    }
}
