using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Infrastructure;

public sealed class MongoHealthCheck(IClickMetaStore store) : IHealthCheck
{
    // See DbContextHealthCheck<TContext>'s identical field: bounded well under docker-compose's
    // healthcheck `timeout: 5s` so this fails fast instead of hanging past Docker's own timeout.
    // Races via Task.WhenAny rather than a CancelAfter token: MongoClickMetaStore.PingAsync's own
    // unconditional `catch { return false; }` swallows any OperationCanceledException a token would
    // produce before it ever reaches this method - a token-based timeout's cancellation branch would
    // be dead code (same lesson RabbitMqHealthCheck's fix already ran into, confirmed here too).
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var pingTask = store.PingAsync(cancellationToken);
            var winner = await Task.WhenAny(pingTask, Task.Delay(Timeout, cancellationToken));

            if (winner != pingTask)
            {
                return HealthCheckResult.Unhealthy($"MongoDB did not respond within {Timeout.TotalSeconds}s.");
            }

            return await pingTask
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("MongoDB is not reachable.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("MongoDB is not reachable.", ex);
        }
    }
}
