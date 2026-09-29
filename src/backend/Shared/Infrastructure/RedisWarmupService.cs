using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Infrastructure;

// LinkApi/RedirectApi's Redis usage - IDistributedCache (AddStackExchangeRedisCache, backing
// LinkCacheService) and, in RedirectApi only, a separate raw IConnectionMultiplexer for click
// counting - connects lazily on first use, same class of problem RabbitMqWarmupService already
// fixed for RabbitMQ (see that file's comment). Confirmed live: right after RabbitMqWarmupService
// made click.tracked publish fast again, a fresh 20-replica redirect-api.resolve run still showed
// GET {hash} averaging ~8s (p95 ~45s, max ~67s) across nearly every request - and re-probing the
// very same replicas a few minutes later (already warm) came back in under 5ms. Same signature as
// the RabbitMQ case: a connection-establishment tax (here, Redis/Sentinel discovery across many
// replicas connecting at once) paid by real traffic instead of at startup.
//
// IConnectionMultiplexer is resolved optionally (IServiceProvider.GetService, not a constructor
// dependency) since only RedirectApi registers one directly - LinkApi only has the IDistributedCache
// registration, so this same service works unmodified in both.
public sealed class RedisWarmupService(
    IDistributedCache cache,
    IServiceProvider services,
    ILogger<RedisWarmupService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await cache.GetAsync("__warmup__", cancellationToken);
        }
        catch (Exception ex)
        {
            // Best-effort only, same reasoning as RabbitMqWarmupService: Redis being briefly
            // unreachable at boot must not stop the host from starting - the app's own cache-miss/
            // Postgres-fallback path (and, for LinkApi, the Cache__Enabled=false path) already handle
            // Redis being down; this service is purely a startup-time optimization for the common
            // case where it's already up (docker-compose gates on the Redis Sentinels being healthy).
            logger.LogWarning(ex, "Redis (IDistributedCache) warmup at startup failed - will connect lazily on first use instead");
        }

        if (services.GetService<IConnectionMultiplexer>() is { } multiplexer)
        {
            try
            {
                await multiplexer.GetDatabase().PingAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Redis (IConnectionMultiplexer) warmup at startup failed - will connect lazily on first use instead");
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
