using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Infrastructure;

// IRabbitMqConnection reconnects lazily and never caches a failed attempt (see RabbitMqClient), so
// the only way to know the broker is reachable right now is to actually open a channel.
public sealed class RabbitMqHealthCheck(IRabbitMqConnection connection) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var channel = await connection.CreateChannelAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("RabbitMQ is not reachable.", ex);
        }
    }
}
