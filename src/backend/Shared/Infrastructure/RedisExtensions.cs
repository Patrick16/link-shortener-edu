using Common;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Infrastructure;

public static class RedisExtensions
{
    // IDistributedCache over Redis (a JSON-blob GET/SET abstraction, backing LinkCacheService) plus
    // the warmup service that opens the connection during startup instead of on the first request.
    //
    // InstanceName is prepended to every key before it reaches Redis - any two services meant to
    // read/write the SAME logical cache entries (same Key(id) format) must configure the SAME
    // InstanceName, or they silently write to disjoint keyspaces despite running identical caching
    // code. LinkApi/RedirectApi/ShortenerService share one (empty) InstanceName for exactly this
    // reason - see sandbox/docs/scenarios/pitfalls/redis-instance-name-breaks-cross-service-cache-
    // sharing.md for the real bug this was found fixing. TrafficService/ReportingService
    // deliberately do the same with a different, unrelated cache ("GeoIpCache" - see
    // pitfalls/geoip-cache-wiring-duplicated-across-services.md).
    public static IHostApplicationBuilder AddRedisDistributedCache(this IHostApplicationBuilder builder)
    {
        builder.Services.AddStackExchangeRedisCache(options =>
        {
            options.Configuration = builder.Configuration.GetConnectionString(Constants.RedisConnectionString);
            options.InstanceName = builder.Configuration[Constants.RedisInstanceName];
        });
        builder.Services.AddHostedService<RedisWarmupService>();

        // EntityCacheService's L1 (in-process) layer, ahead of this IDistributedCache (L2) - see
        // EntityCacheService's own class-level comment. AddMemoryCache() is already idempotent
        // (uses TryAdd internally), so calling this alongside the registration above is harmless
        // even on a service (ShortenerService) that wires this up more than once indirectly.
        builder.Services.AddMemoryCache();

        builder.AddRedisCircuitBreaker();

        return builder;
    }

    // Factored out of AddRedisDistributedCache so a caller that needs the breaker but not the
    // IDistributedCache/MemoryCache/warmup machinery (RedisRateLimiter, via AddRedisRateLimiter
    // below) doesn't have to pull all of that in just to get it - AuthApi has never used Redis
    // for caching and shouldn't gain a cache abstraction, a memory cache, and a warmup worker it
    // has no other use for just to rate-limit.
    //
    // Shared by EntityCacheService/LinkCacheService (via AddRedisDistributedCache),
    // CachingGeoIpResolver (wherever AddGeoIpResolution is called), and now RedisRateLimiter - all
    // three wrap calls into Redis. Keyed, not a plain singleton: a process can also register a
    // gRPC/RabbitMQ breaker (see GrpcMessagingExtensions/RabbitMqExtensions) and ResiliencePipeline
    // has no other way to tell them apart by type alone. TryAdd, not Add - safe to call from more
    // than one of these extension methods on the same builder.
    private static IHostApplicationBuilder AddRedisCircuitBreaker(this IHostApplicationBuilder builder)
    {
        builder.Services.TryAddKeyedSingleton(Constants.RedisCircuitBreakerKey, (sp, key) =>
            ResiliencePipelines.BuildCircuitBreaker(
                (string)key!, sp.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ResiliencePipelines).FullName!),
                ResiliencePipelines.CircuitBreakersEnabled(sp.GetRequiredService<IConfiguration>())));
        return builder;
    }

    // Raw multiplexer for the sorted-set and pub/sub commands IDistributedCache can't express.
    // Shared by RedirectApi (writes per-user day buckets, reads deletion markers) and ReportingApi
    // (reads the buckets and subscribes to change notifications).
    // AbortOnConnectFail=false: with the default (true), Connect throws when Sentinel isn't reachable
    // yet, which takes the whole host down on startup - a Redis blip at boot must not stop an API
    // that can serve everything else. The multiplexer keeps retrying in the background instead.
    // TryAddSingleton, not AddSingleton: a host (e.g. RedirectApi) can reach this through more than
    // one caller (AddClickCounter and AddLinkDeletionMarkerStore both need it) - idempotent
    // registration means whichever runs first wins, instead of opening a second, wasted connection.
    public static IHostApplicationBuilder AddRedisMultiplexer(this IHostApplicationBuilder builder)
    {
        var connectionString = builder.Configuration.GetConnectionString(Constants.RedisConnectionString);
        var options = ConfigurationOptions.Parse(connectionString!);
        options.AbortOnConnectFail = false;
        builder.Services.TryAddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(options));
        return builder;
    }

    // Shared by LinkApi (writes the optimistic marker on DELETE), RedirectApi (reads it on every
    // resolve) and ShortenerService (confirms it from the consumer and the TTL sweep job) - all
    // three need it wired identically, so it lives here rather than being duplicated three times.
    // AddRedisCircuitBreaker() added here (bug found live, 2026-10-29, alongside
    // RedisLinkDeletionMarkerStore's own missing Cache:Enabled bypass): the keyed ResiliencePipeline
    // RedisLinkDeletionMarkerStore now takes was only ever registered by whichever OTHER extension
    // (AddRedisDistributedCache/AddRedisRateLimiter) a given Program.cs happened to also call - every
    // current caller does, so this worked by accident, not by this method actually declaring its own
    // dependency. Same self-sufficient, TryAdd-idempotent shape AddRedisRateLimiter already uses.
    public static IHostApplicationBuilder AddLinkDeletionMarkerStore(this IHostApplicationBuilder builder)
    {
        builder.AddRedisMultiplexer();
        builder.AddRedisCircuitBreaker();
        builder.Services.AddSingleton<ILinkDeletionMarkerStore, RedisLinkDeletionMarkerStore>();
        return builder;
    }

    // LinkApi only (POST /Links's Idempotency-Key support) - see IIdempotencyStore's own comment
    // for the two-tier shape, deliberately mirroring AddLinkDeletionMarkerStore above.
    public static IHostApplicationBuilder AddIdempotencyStore(this IHostApplicationBuilder builder)
    {
        builder.AddRedisMultiplexer();
        builder.Services.AddSingleton<IIdempotencyStore, RedisIdempotencyStore>();
        return builder;
    }

    // AuthApi and RedirectApi (see WebDefaults.RedisRateLimitFilter for how each wires its own
    // policy on top of this). Idempotent via AddRedisMultiplexer/AddRedisCircuitBreaker's own
    // TryAdd* calls - safe to call alongside AddRedisDistributedCache/AddLinkDeletionMarkerStore
    // on a service (RedirectApi) that already pulls in the multiplexer and breaker for other
    // reasons, and cheap enough on its own for a service (AuthApi) that previously had no Redis
    // dependency at all.
    public static IHostApplicationBuilder AddRedisRateLimiter(this IHostApplicationBuilder builder)
    {
        builder.AddRedisMultiplexer();
        builder.AddRedisCircuitBreaker();
        builder.Services.AddSingleton<IRedisRateLimiter, RedisRateLimiter>();
        // RedisRateLimitFilter's RateLimitFailureMode.LocalFallback dependency - registered here
        // (not conditionally, per app) since it's cheap to construct and unused unless a policy
        // actually opts into LocalFallback; same "always wire it, let the policy decide whether it's
        // ever called" shape as the breaker above.
        builder.Services.AddSingleton<ILocalFallbackRateLimiter, LocalFallbackRateLimiter>();
        return builder;
    }
}
