using Microsoft.Extensions.Caching.Hybrid;

namespace Common;

// Alternative to EntityCacheService<T> - built on the BCL's own HybridCache instead of hand-
// rolling L1 (in-process) + L2 (Redis) layering and single-flight stampede protection the way
// EntityCacheService does (see that class's own comments for exactly what it hand-rolls and why).
// Deliberately minimal, not full parity - see .notes/PLAN.md's Simplification Backlog item 1:
//   - Negative caching comes along for free, but on different terms than EntityCacheService's own
//     deliberate design. Confirmed by direct testing: HybridCache DOES cache a null GetOrFetch
//     result (the factory isn't re-invoked on the next call for the same key) - but with the SAME
//     TTL as any other entry (DefaultTtlSeconds below), not EntityCacheService's deliberately
//     short, separate 10s negative-cache window. A key that doesn't exist yet but will shortly
//     (e.g. right after a 404 for a link about to be created) stays "not found" here for however
//     long a normal cache entry lives, not 10s - a real behavioral difference, not just a missing
//     feature.
//   - No Redis circuit breaker. EntityCacheService wraps every Redis call in this project's own
//     Polly pipeline to fail open on a dead Redis; this class makes no such attempt and relies on
//     whatever HybridCache itself does against its registered IDistributedCache.
//   - Cache:Enabled/Cache:L1Enabled/Cache:NegativeEnabled (the control panel's cache-demo toggles)
//     are EntityCacheService-only - this class has no equivalent switches and is always "on" once
//     Cache:Backend selects it (see EntityCacheServiceExtensions.AddLinkCacheService).
// Selected via DI, not a code edit - see EntityCacheServiceExtensions.AddLinkCacheService.
public abstract class HybridEntityCacheService<T>(HybridCache cache) : IEntityCacheService<T> where T : class
{
    // Matches EntityCacheService's own CacheAsync default - swapping the DI registration shouldn't
    // silently change the effective TTL for a caller relying on this parameter's default.
    private const int DefaultTtlSeconds = 3600;

    // Same +-10% spread as EntityCacheService.ApplyTtlJitter, for the same reason (see that
    // method's own comment): without it, a burst of same-time writes - a k6 load test, or
    // ShortenerService pre-warming the cache on a batch consume - would all expire in lockstep and
    // reproduce the exact synchronized DB-miss spike jitter exists to avoid. Found missing by
    // review (.notes/review-reports/2026-11-05-1420-hybridcache-backend-toggle.md, F1).
    private const double TtlJitterFraction = 0.1;

    protected abstract string Key(string id);

    public async Task CacheAsync(T entity, string id, CancellationToken cancellationToken, int ttlSeconds = DefaultTtlSeconds)
    {
        await cache.SetAsync(Key(id), entity, EntryOptions(ttlSeconds), cancellationToken: cancellationToken);
    }

    public async Task InvalidateCacheAsync(string id)
    {
        await cache.RemoveAsync(Key(id));
    }

    public async Task<T?> GetCachedAsync(string id, CancellationToken cancellationToken)
    {
        // HybridCache has no "read only if present" API on purpose (it steers callers toward
        // GetOrCreateAsync's coalescing instead) - a factory that always throws on an actual miss
        // is the documented way to get that behavior back: it only ever runs when nothing is
        // cached yet, and since it throws instead of returning, nothing gets written either.
        // CAUTION: this uses the same cache key as GetOrFetch below, and HybridCache's single-
        // flight coalescing is per-key, not per-caller - a concurrent GetOrFetch call for the same
        // id could coalesce onto this method's throwing factory and wrongly receive null instead of
        // running the real DB fetch. No production caller does this today (only GetOrFetch/
        // CacheAsync/InvalidateCacheAsync are used) - flagged by review (see F2 in
        // .notes/review-reports/2026-11-05-1420-hybridcache-backend-toggle.md) so a future caller
        // doesn't trip over it.
        try
        {
            return await cache.GetOrCreateAsync<T?>(
                Key(id),
                static _ => throw new CacheMissException(),
                cancellationToken: cancellationToken);
        }
        catch (CacheMissException)
        {
            return null;
        }
    }

    public async Task<T?> GetOrFetch(string id, Func<Task<T?>> fetchFromDb, CancellationToken cancellationToken)
    {
        // fetchFromDb is the TState here (not captured in a closure) so concurrent callers for the
        // same id share one real factory invocation via HybridCache's own built-in single-flight
        // coalescing - the ConcurrentDictionary<string, Lazy<Task<T?>>> EntityCacheService hand-
        // rolls for this is exactly what HybridCache absorbs.
        return await cache.GetOrCreateAsync(
            Key(id),
            fetchFromDb,
            static (fetch, _) => new ValueTask<T?>(fetch()),
            EntryOptions(DefaultTtlSeconds),
            cancellationToken: cancellationToken);
    }

    private static HybridCacheEntryOptions EntryOptions(int ttlSeconds) =>
        new() { Expiration = TimeSpan.FromSeconds(ApplyTtlJitter(ttlSeconds)) };

    // Random.Shared, not a seeded/injected instance - same reasoning as EntityCacheService's own
    // copy of this method: a fixed seed would defeat the entire point of decorrelating
    // concurrently-created entries.
    private static double ApplyTtlJitter(int ttlSeconds) =>
        ttlSeconds * (1 + ((Random.Shared.NextDouble() * 2) - 1) * TtlJitterFraction);

    private sealed class CacheMissException : Exception;
}
