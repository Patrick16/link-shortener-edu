using Common;
using Common.Models;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Infrastructure;

// DI wiring for the cache-backend toggle - same "one config value, read once at startup, nothing
// downstream branches on it again" shape as GrpcMessagingExtensions' Messaging:Mode toggle.
public static class EntityCacheServiceExtensions
{
    private const string LegacyBackend = "legacy";
    private const string HybridBackend = "hybrid";

    // Single source of truth for parsing Cache:Backend - same fail-loudly-on-a-typo reasoning as
    // GrpcMessagingExtensions.IsMessagingGrpcMode.
    public static bool IsHybridCacheBackend(this IConfiguration configuration)
    {
        var backend = configuration[Constants.CacheBackendSection];
        if (string.IsNullOrEmpty(backend) || string.Equals(backend, LegacyBackend, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(backend, HybridBackend, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        throw new InvalidOperationException(
            $"Unrecognized {Constants.CacheBackendSection} value '{backend}' - expected '{LegacyBackend}' or '{HybridBackend}'.");
    }

    // LinkApi, RedirectApi, ShortenerService - each one's single IEntityCacheService<Link>
    // dependency is either LinkCacheService (default, hand-rolled) or HybridLinkCacheService
    // (Microsoft.Extensions.Caching.Hybrid), selected once here by Cache:Backend. Requires
    // AddRedisDistributedCache to already have registered IDistributedCache - HybridCache
    // auto-discovers it as its own L2, the same Redis both backends end up reading/writing.
    public static IHostApplicationBuilder AddLinkCacheService(this IHostApplicationBuilder builder)
    {
        if (builder.Configuration.IsHybridCacheBackend())
        {
            builder.Services.AddHybridCache();
            builder.Services.AddSingleton<IEntityCacheService<Link>, HybridLinkCacheService>();
        }
        else
        {
            builder.Services.AddSingleton<IEntityCacheService<Link>, LinkCacheService>();
        }

        return builder;
    }
}
