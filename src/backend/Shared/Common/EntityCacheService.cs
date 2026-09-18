using Microsoft.Extensions.Caching.Distributed;
using System.Text.Json;

namespace Common;

public abstract class EntityCacheService<T>(IDistributedCache cache) : IEntityCacheService<T> where T : class
{
    protected readonly IDistributedCache _cache = cache;
    protected static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    protected abstract string Key(string id);

    public async Task CacheAsync(T entity, string id, CancellationToken cancellationToken, int ttlSeconds = 3600)
    {
        string json = JsonSerializer.Serialize(entity, JsonOptions);
        var options = new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(ttlSeconds)
        };
        await _cache.SetStringAsync(Key(id), json, options, cancellationToken);
    }

    public async Task InvalidateCacheAsync(string id) => await _cache.RemoveAsync(Key(id));

    public async Task<T?> GetCachedAsync(string id, CancellationToken cancellationToken)
    {
        string? json = await _cache.GetStringAsync(Key(id), cancellationToken);
        return json is null ? null : JsonSerializer.Deserialize<T>(json, JsonOptions);
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
