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
    // See RabbitMqWarmupService's identical field for why this exists: a hang here stalls the whole
    // container's startup (IHostedService.StartAsync blocks Kestrel from listening, and
    // HostOptions.StartupTimeout is unbounded by default), unlike a health check which just fails one
    // probe. Both inner warmup attempts already dispose/swallow after themselves, so letting this
    // fire and moving on is safe - a slow Redis trades "no warmup benefit this boot" for "the
    // container still starts on time."
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var warmup = Task.WhenAll(
            WarmupDistributedCacheAsync(cancellationToken),
            WarmupConnectionMultiplexerAsync());
        var winner = await Task.WhenAny(warmup, Task.Delay(Timeout, cancellationToken));

        if (winner != warmup)
        {
            logger.LogWarning(
                "Redis warmup did not complete within {TimeoutSeconds}s - continuing startup without it, will connect lazily on first use instead",
                Timeout.TotalSeconds);
        }
    }

    private async Task WarmupDistributedCacheAsync(CancellationToken cancellationToken)
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
    }

    private async Task WarmupConnectionMultiplexerAsync()
    {
        try
        {
            // GetService itself runs the synchronous, blocking ConnectionMultiplexer.Connect(...) for
            // this singleton's factory - it throws (RedisConnectionException) when Sentinel can't
            // resolve the master yet, and an unhandled throw here used to abort host startup
            // (observed live: redirect-api exited 139 on a cold `docker compose up`; a failed factory
            // isn't cached, so the next real use just retries). Offloaded to a pool thread via
            // Task.Run so the Timeout race in StartAsync can actually bound it - a plain synchronous
            // call never yields back to let Task.WhenAny observe the Delay while it's running.
            var multiplexer = await Task.Run(() => services.GetService<IConnectionMultiplexer>());
            if (multiplexer is not null)
            {
                await multiplexer.GetDatabase().PingAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Redis (IConnectionMultiplexer) warmup at startup failed - will connect lazily on first use instead");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
