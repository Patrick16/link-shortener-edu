using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Common;

public abstract class EntityCacheService<T>(IDistributedCache cache, IConfiguration configuration, ILogger<EntityCacheService<T>> logger) : IEntityCacheService<T> where T : class
{
    protected readonly IDistributedCache _cache = cache;
    protected static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // Coalesces concurrent GetOrFetch misses for the same key into a single DB fetch + cache write
    // (see GetOrFetch). Entries are removed as soon as the fetch they represent completes, so this
    // never grows past the number of keys currently mid-fetch.
    private readonly ConcurrentDictionary<string, Lazy<Task<T?>>> _inFlightFetches = new();

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
        if (cached is not null)
        {
            // Debug, not Information - this runs on every LinkApi/RedirectApi request, same volume
            // concern as those controllers. Flip to Debug locally to see the cache hit/miss split
            // that the control panel's cache toggle experiment is all about.
            _logger.LogDebug("Cache hit for {Key}", Key(id));
            return cached;
        }

        var key = Key(id);
        // Without this, every concurrent caller that misses the cache for the same key runs its own
        // fetchFromDb() and its own cache write - a burst of requests for a hash that just expired
        // hits the DB once per request instead of once total. GetOrAdd's own factory can itself be
        // invoked more than once under contention, so the factory is wrapped in a
        // Lazy<Task<T?>>(ExecutionAndPublication) to guarantee fetchFromDb runs exactly once even
        // when multiple threads race into GetOrAdd for the same brand-new key. All joiners share the
        // first caller's cancellationToken for the fetch+cache write; a caller that cancels while
        // others are still waiting on the same in-flight fetch will cancel it for them too - an
        // accepted tradeoff of coalescing, not a bug.
        var lazyFetch = _inFlightFetches.GetOrAdd(
            key,
            _ => new Lazy<Task<T?>>(
                () => FetchAndCacheAsync(id, fetchFromDb, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await lazyFetch.Value;
        }
        finally
        {
            _inFlightFetches.TryRemove(key, out _);
        }
    }

    private async Task<T?> FetchAndCacheAsync(string id, Func<Task<T?>> fetchFromDb, CancellationToken cancellationToken)
    {
        var entity = await fetchFromDb();
        _logger.LogDebug("Cache miss for {Key}, fetched from DB", Key(id));
        if (entity is not null) await CacheAsync(entity, id, cancellationToken);
        return entity;
    }
}
