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
    public async Task StartAsync(CancellationToken cancellationToken)
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
