using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Common;

public abstract class EntityCacheService<T>(IDistributedCache cache, IConfiguration configuration, ILogger<EntityCacheService<T>> logger) : IEntityCacheService<T> where T : class
{
    protected readonly IDistributedCache _cache = cache;
    protected static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // Cache__Enabled=false is the control panel's "disable caching" toggle - checked before ever
    // touching Redis so the demo is an instant, clean bypass (always a cache miss, straight to the
    // DB) rather than every request eating a failed-connection timeout against a stopped container.
    private readonly bool _cacheEnabled = !bool.TryParse(configuration["Cache:Enabled"], out var cacheEnabled) || cacheEnabled;
    private readonly ILogger<EntityCacheService<T>> _logger = logger;

    protected abstract string Key(string id);

    public async Task CacheAsync(T entity, string id, CancellationToken cancellationToken, int ttlSeconds = 3600)
    {
        if (!_cacheEnabled) return;

        try
        {
            string json = JsonSerializer.Serialize(entity, JsonOptions);
            var options = new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(ttlSeconds)
            };
            await _cache.SetStringAsync(Key(id), json, options, cancellationToken);
        }
        catch (Exception ex)
        {
            // Populating the cache is an optimization, not a requirement - a Redis outage (chaos
            // testing, or the cache toggle) should degrade to "every read goes to the DB", not fail
            // the write/read that triggered this.
            _logger.LogWarning(ex, "Failed to populate cache for {Key} - continuing without it", Key(id));
        }
    }

    public async Task InvalidateCacheAsync(string id)
    {
        if (!_cacheEnabled) return;

        try
        {
            await _cache.RemoveAsync(Key(id));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to invalidate cache for {Key} - a stale entry may remain until it expires", Key(id));
        }
    }

    public async Task<T?> GetCachedAsync(string id, CancellationToken cancellationToken)
    {
        if (!_cacheEnabled) return null;

        try
        {
            string? json = await _cache.GetStringAsync(Key(id), cancellationToken);
            return json is null ? null : JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read cache for {Key} - treating as a cache miss", Key(id));
            return null;
        }
    }

    public async Task<T?> GetOrFetch(string id, Func<Task<T?>> fetchFromDb, CancellationToken cancellationToken)
    {
        var cached = await GetCachedAsync(id, cancellationToken);
        if (cached is not null) return cached;

        var entity = await fetchFromDb();
        if (entity is not null) await CacheAsync(entity, id, cancellationToken);
        return entity;
    }
}
