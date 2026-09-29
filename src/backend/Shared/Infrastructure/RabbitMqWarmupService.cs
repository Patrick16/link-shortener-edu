using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure;

// LinkApi/RedirectApi only ever touch RabbitMQ lazily (RabbitMqClient's own doc comment: the
// connection is established on first use, not in the constructor). Unlike ShortenerService/
// TrafficService's RabbitMqConsumer - which needs a connection from the moment it starts consuming,
// so it's never "cold" - a publish-only service has no such forcing function: whichever request
// happens to be first after a fresh start (or the first of many replicas all connecting to RabbitMQ
// at once during a scale-up burst) pays for establishing that connection inline. Confirmed live: a
// RedirectApi replica's click.tracked publish took 32s under exactly this condition, and the same
// class of delay showed up on /health/ready before RabbitMqHealthCheck's own fix bounded it.
//
// IHostedService.StartAsync runs before Kestrel starts accepting connections (as long as this is
// registered before the framework's own web-host service - true for a plain AddHostedService call
// in Program.cs), so this pays that one-time connection-establishment cost during container startup,
// off any real request's critical path, instead of on whichever caller is first or unlucky.
public sealed class RabbitMqWarmupService(IRabbitMqConnection connection, ILogger<RabbitMqWarmupService> logger) : IHostedService
{
    // Bounds how long a slow-but-not-fully-down broker can delay container startup. Unlike a health
    // check (which just fails one probe), a hang *here* stalls the whole container: StartAsync blocks
    // Kestrel from listening until it returns, and HostOptions.StartupTimeout is unbounded by default
    // - confirmed live elsewhere in this investigation that a connection-establishment burst can take
    // 13-67s under load (see sandbox/docs/pgcat-pool-sizing.md). When this fires, the warmup attempt
    // itself isn't cancelled - WarmupAsync already disposes its own channel and swallows its own
    // exceptions, so it's safe to just stop *waiting* for it and let startup proceed; a slow broker
    // trades "no warmup benefit this boot" for "the container still starts on time" instead of "the
    // container never becomes ready."
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var warmup = WarmupAsync(cancellationToken);
        var winner = await Task.WhenAny(warmup, Task.Delay(Timeout, cancellationToken));

        if (winner != warmup)
        {
            logger.LogWarning(
                "RabbitMQ warmup did not complete within {TimeoutSeconds}s - continuing startup without it, will connect lazily on first use instead",
                Timeout.TotalSeconds);
        }
    }

    private async Task WarmupAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var channel = await connection.CreateChannelAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // Best-effort only - RabbitMQ being briefly unreachable at boot must not stop the host
            // from starting. RabbitMqClient's own lazy reconnect, plus the SQLite fallback + retry
            // worker, already handle that case; this service is purely a startup-time optimization
            // for the common case where the broker is already up (docker-compose's own
            // `depends_on: rabbitmq: condition: service_healthy` normally guarantees exactly that).
            logger.LogWarning(ex, "RabbitMQ warmup at startup failed - will connect lazily on first use instead");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
