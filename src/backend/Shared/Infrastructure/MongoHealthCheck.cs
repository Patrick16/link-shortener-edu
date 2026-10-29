using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Infrastructure;

// Generic over IPingable, the same shape as ClickHouseHealthCheck<TPingable> - IClickMetaStore in
// TrafficService (write) and IClickMetaQueryService in ReportingApi (read) are both Mongo-backed
// and share this one health check instead of two near-identical copies.
public sealed class MongoHealthCheck<TPingable>(TPingable pingable) : IHealthCheck
    where TPingable : IPingable
{
    // See ClickHouseHealthCheck<TPingable>'s identical field/reasoning - bounded well under
    // docker-compose's healthcheck `timeout: 5s`.
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
