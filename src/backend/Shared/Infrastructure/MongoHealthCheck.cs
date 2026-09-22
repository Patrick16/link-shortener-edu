using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Infrastructure;

public sealed class MongoHealthCheck(IClickMetaStore store) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var reachable = await store.PingAsync(cancellationToken);
        return reachable
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("MongoDB is not reachable.");
    }
}
