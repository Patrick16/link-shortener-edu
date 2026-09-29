using Common;
using Infrastructure;
using StackExchange.Redis;

namespace RedirectApi;

// RedirectApi-only wiring - the parts of Program.cs that no other service shares.
public static class RedirectApiServiceExtensions
{
    public static WebApplicationBuilder AddRedirectServices(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IEntityCacheService<Common.Models.Link>, LinkCacheService>();
        return builder;
    }

    // Separate from the IDistributedCache registration (AddRedisDistributedCache: a JSON-blob GET/SET
    // abstraction with no atomic increment) - this is the raw client the click counter needs for INCR.
    public static WebApplicationBuilder AddClickCounter(this WebApplicationBuilder builder)
    {
        var redisConnectionString = builder.Configuration.GetConnectionString(Constants.RedisConnectionString);
        builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConnectionString!));
        builder.Services.AddSingleton<IClickCounterService, RedisClickCounterService>();
        return builder;
    }
}
