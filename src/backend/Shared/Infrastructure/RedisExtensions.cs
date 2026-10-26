using Common;
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

        // Shared by EntityCacheService/LinkCacheService (this IDistributedCache) and, in whichever
        // service also calls AddGeoIpResolution, CachingGeoIpResolver - same registration either
        // way, since both wrap calls into Redis. Keyed, not a plain singleton: a process can also
        // register a gRPC/RabbitMQ breaker (see GrpcMessagingExtensions/RabbitMqExtensions) and
        // ResiliencePipeline has no other way to tell them apart by type alone.
        builder.Services.TryAddKeyedSingleton(Constants.RedisCircuitBreakerKey, (sp, key) =>
            ResiliencePipelines.BuildCircuitBreaker(
                (string)key!, sp.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ResiliencePipelines).FullName!)));

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
    public static IHostApplicationBuilder AddLinkDeletionMarkerStore(this IHostApplicationBuilder builder)
    {
        builder.AddRedisMultiplexer();
        builder.Services.AddSingleton<ILinkDeletionMarkerStore, RedisLinkDeletionMarkerStore>();
        return builder;
    }
}
