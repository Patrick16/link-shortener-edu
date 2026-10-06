using Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
        return builder;
    }
}
