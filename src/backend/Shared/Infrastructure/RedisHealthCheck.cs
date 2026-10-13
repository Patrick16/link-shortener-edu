using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace Infrastructure;

public sealed class RedisHealthCheck(IConnectionMultiplexer connectionMultiplexer) : IHealthCheck
{
    // Same bound and same Task.WhenAny race as MongoHealthCheck - a token-based timeout would be
    // swallowed by the client's own failure handling before it reached this method.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    private readonly IConnectionMultiplexer _connectionMultiplexer = connectionMultiplexer;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var pingTask = _connectionMultiplexer.GetDatabase().PingAsync();
            var winner = await Task.WhenAny(pingTask, Task.Delay(Timeout, cancellationToken));

            if (winner != pingTask)
            {
                return HealthCheckResult.Unhealthy($"Redis did not respond within {Timeout.TotalSeconds}s.");
            }

            await pingTask;
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Redis is not reachable.", ex);
        }
    }
}
