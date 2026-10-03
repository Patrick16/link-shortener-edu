using Microsoft.Extensions.Diagnostics.HealthChecks;
using RabbitMQ.Client;

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
        // Fast path: skip opening (and immediately disposing) a brand-new channel when the shared
        // connection is already known to be open. Without this, every single probe paid the full
        // CreateChannelAsync round trip - under a connection-establishment burst elsewhere on the same
        // shared connection (see the comment below), that round trip consistently lands on this
        // method's own 3s Timeout instead of actually completing fast: a live run showed
        // /health/ready's p95 tracking this Timeout almost exactly (~3000ms) under load, for a
        // connection that was otherwise healthy. IsOpen is the same signal RabbitMqClient.IsUsable
        // already trusts to tell "still good" apart from "was good, now dead", so reusing it here
        // doesn't weaken what this check actually verifies.
        if (connection.IsOpen)
        {
            return HealthCheckResult.Healthy();
        }

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
                            return DisposeQuietlyAsync(t.Result);
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

    // Best-effort cleanup of an already-abandoned channel - guarded the same way the failure branch
    // observes its own fault, since DisposeAsync() itself throwing here would otherwise surface as an
    // unobserved task exception with nothing left that could act on it anyway.
    private static async Task DisposeQuietlyAsync(IChannel channel)
    {
        try
        {
            await channel.DisposeAsync();
        }
        catch
        {
        }
    }
}
