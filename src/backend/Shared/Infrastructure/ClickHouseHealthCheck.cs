using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Infrastructure;

// Generic over IPingable, the same shape as DbContextHealthCheck<TContext> - constructor-injected
// with whichever ClickHouse-backed dependency the service actually registered (IClickFactStore in
// ReportingService, IClickFactQueryService in ReportingApi), resolved from DI like every other
// health check here. Replaces an earlier delegate-based version (found during review to be
// inconsistent with this file's own established pattern) that existed only because IClickFactStore/
// IClickFactQueryService used to each declare their own identical PingAsync with no shared
// interface to be generic over - seeing IPingable.
public sealed class ClickHouseHealthCheck<TPingable>(TPingable pingable) : IHealthCheck
    where TPingable : IPingable
{
    // See MongoHealthCheck's identical field/reasoning - bounded well under docker-compose's
    // healthcheck `timeout: 5s`.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var pingTask = pingable.PingAsync(cancellationToken);
            var winner = await Task.WhenAny(pingTask, Task.Delay(Timeout, cancellationToken));

            if (winner != pingTask)
            {
                return HealthCheckResult.Unhealthy($"ClickHouse did not respond within {Timeout.TotalSeconds}s.");
            }

            return await pingTask
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("ClickHouse is not reachable.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("ClickHouse is not reachable.", ex);
        }
    }
}
