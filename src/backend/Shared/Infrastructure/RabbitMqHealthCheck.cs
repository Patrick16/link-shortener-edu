using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Infrastructure;

// IRabbitMqConnection reconnects lazily and never caches a failed attempt (see RabbitMqClient), so
// the only way to know the broker is reachable right now is to actually open a channel.
public sealed class RabbitMqHealthCheck(IRabbitMqConnection connection) : IHealthCheck
{
    // See DbContextHealthCheck<TContext>'s identical field: bounded well under docker-compose's
    // healthcheck `timeout: 5s` so a stuck connection fails the probe fast instead of hanging past
    // Docker's own timeout anyway.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Confirmed live (200-VU run, RabbitMQ under a connection-establishment burst):
            // CreateChannelAsync can end up awaiting the *shared* connection-establishment task other
            // callers started (RabbitMqClient.GetConnectionAsync's `_connection`, reused across every
            // caller so a health check doesn't open its own extra connection) - RabbitMQ.Client does
            // not reliably abort that handshake just because this one caller's token got cancelled, so
            // a passed-in CancellationToken alone left this taking 9-15s+ instead of 3s (server logs:
            // "RabbitMQ did not respond within 3s" completing after 13145ms). Racing with a timeout
            // task instead bounds *this probe's* wait without touching the shared connection attempt,
            // which keeps running in the background for whoever actually needs it (a publisher/
            // consumer) to eventually succeed. See sandbox/docs/pgcat-pool-sizing.md.
            var channelTask = connection.CreateChannelAsync(cancellationToken);
            var winner = await Task.WhenAny(channelTask, Task.Delay(Timeout, cancellationToken));

            if (winner != channelTask)
            {
                // Don't abandon the loser: if it eventually succeeds, it hands back a live IChannel
                // that nothing else will ever dispose (a leaked client-side channel plus a slot in the
                // broker's per-connection channel table) - observe it instead, on whichever thread the
                // continuation runs on, well after this method has already returned Unhealthy.
                _ = channelTask.ContinueWith(
                    static t =>
                    {
                        if (t.IsCompletedSuccessfully)
                        {
                            return t.Result.DisposeAsync().AsTask();
                        }

                        _ = t.Exception; // observe the fault so it never surfaces as unobserved later
                        return Task.CompletedTask;
                    },
                    TaskScheduler.Default).Unwrap();
                return HealthCheckResult.Unhealthy($"RabbitMQ did not respond within {Timeout.TotalSeconds}s.");
            }

            await using var channel = await channelTask;
            return HealthCheckResult.Healthy();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy($"RabbitMQ did not respond within {Timeout.TotalSeconds}s.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("RabbitMQ is not reachable.", ex);
        }
    }
}
