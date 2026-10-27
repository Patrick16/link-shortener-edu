using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polly;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Common;

public abstract class EntityCacheService<T>(
    IDistributedCache cache,
    IConfiguration configuration,
    ILogger<EntityCacheService<T>> logger,
    [FromKeyedServices(Constants.RedisCircuitBreakerKey)] ResiliencePipeline resiliencePipeline,
    IMemoryCache memoryCache) : IEntityCacheService<T> where T : class
{
    protected readonly IDistributedCache _cache = cache;
    protected static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // L1 (in-process) ahead of Redis (L2) - a hot key resolves in-process instead of paying a
    // network round trip at all. Deliberately a short, fixed TTL and NOT cross-replica-invalidated:
    // InvalidateCacheAsync below only ever clears THIS process's L1 copy. Another replica's L1
    // entry for the same key keeps serving the old value until its own TTL expires - a documented,
    // accepted staleness window (same "known limitation" category as this project's other
    // display-counter/eventual-consistency tradeoffs), not a bug to chase. Cross-replica pub/sub
    // invalidation would close it, at the cost of a subscriber in every process - not built, since
    // demonstrating this exact staleness window is the point of this pattern, not something to
    // engineer away immediately.
    private readonly IMemoryCache _l1Cache = memoryCache;
    private static readonly TimeSpan L1Ttl = TimeSpan.FromSeconds(2);

    // Cache__L1Enabled=false is the control panel's "L1 on/off" toggle, independent of the overall
    // Cache:Enabled below - lets the demo compare "Redis only" against "L1 + Redis" directly. Gated
    // by _cacheEnabled too (see PopulateL1) so turning overall caching off still means no layer
    // caches anything, L1 included.
    private readonly bool _l1CacheEnabled = !bool.TryParse(configuration["Cache:L1Enabled"], out var l1CacheEnabled) || l1CacheEnabled;

    // A dead Redis still means every call pays ConnectTimeout/SyncTimeout before this class's own
    // try/catch below turns it into a fail-open no-op - the breaker's job is to skip straight to
    // that no-op once Redis is known-dead, instead of re-discovering it on every single request.
    // BrokenCircuitException is an Exception like any other, so it's caught by the exact same
    // catch blocks already here - no separate handling needed for "breaker open" vs. "Redis
    // actually threw".
    private readonly ResiliencePipeline _resilience = resiliencePipeline;

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

        PopulateL1(id, entity);

        try
        {
            string json = JsonSerializer.Serialize(entity, JsonOptions);
            var options = new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(ttlSeconds)
            };
            await _resilience.ExecuteAsync(
                ct => new ValueTask(_cache.SetStringAsync(Key(id), json, options, ct)), cancellationToken);
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
        // Local eviction only - always safe and cheap (a Remove on an absent key is a no-op) even
        // if L1/overall caching is currently disabled, so this isn't gated behind either flag.
        // See the class-level comment on _l1Cache for why this does NOT reach other replicas.
        _l1Cache.Remove(Key(id));

        if (!_cacheEnabled) return;

        try
        {
            await _resilience.ExecuteAsync(ct => new ValueTask(_cache.RemoveAsync(Key(id), ct)), CancellationToken.None);
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
            string? json = await _resilience.ExecuteAsync(
                ct => new ValueTask<string?>(_cache.GetStringAsync(Key(id), ct)), cancellationToken);
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
        var key = Key(id);

        if (_l1CacheEnabled && _l1Cache.TryGetValue(key, out T? l1Hit))
        {
            // Debug, not Information - see the matching note on the Redis/L2 hit below.
            _logger.LogDebug("L1 cache hit for {Key}", key);
            return l1Hit;
        }

        var cached = await GetCachedAsync(id, cancellationToken);
        if (cached is not null)
        {
            // Debug, not Information - this runs on every LinkApi/RedirectApi request, same volume
            // concern as those controllers. Flip to Debug locally to see the cache hit/miss split
            // that the control panel's cache toggle experiment is all about.
            _logger.LogDebug("Cache hit for {Key}", key);
            PopulateL1(id, cached);
            return cached;
        }

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

    // Gated on both flags (not just _l1CacheEnabled) - CacheAsync already returns early when
    // overall caching is off, but GetOrFetch's own Redis-hit branch calls this directly, so the
    // check is repeated here rather than trusted to every caller.
    private void PopulateL1(string id, T entity)
    {
        if (!_cacheEnabled || !_l1CacheEnabled) return;

        _l1Cache.Set(Key(id), entity, L1Ttl);
    }
}
