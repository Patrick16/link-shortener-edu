using Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Infrastructure;

public static class RedisExtensions
{
    // IDistributedCache over Redis (a JSON-blob GET/SET abstraction, backing LinkCacheService) plus
    // the warmup service that opens the connection during startup instead of on the first request.
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
