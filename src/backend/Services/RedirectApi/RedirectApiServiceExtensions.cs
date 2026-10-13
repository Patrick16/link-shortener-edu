using Common;
using Infrastructure;

namespace RedirectApi;

// RedirectApi-only wiring - the parts of Program.cs that no other service shares.
public static class RedirectApiServiceExtensions
{
    public static WebApplicationBuilder AddRedirectServices(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IEntityCacheService<Common.Models.Link>, LinkCacheService>();
        return builder;
    }

    // Per-user click buckets for the ReportingApi top-links view. Separate from the IDistributedCache
    // registration, which is a JSON-blob abstraction with no sorted-set or pub/sub commands.
    public static WebApplicationBuilder AddClickCounter(this WebApplicationBuilder builder)
    {
        builder.AddRedisMultiplexer();
        builder.Services.AddSingleton<IClickCounterService, RedisClickCounterService>();
        return builder;
    }
}
