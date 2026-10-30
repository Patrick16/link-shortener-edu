using System.Collections.Concurrent;
using System.Threading.RateLimiting;

namespace Infrastructure;

// Exists for exactly one scenario: RedisRateLimitFilter's RateLimitFailureMode.LocalFallback,
// policy-chosen for AuthApi (bug found live, 2026-10-29 topology QA pass) - FailClosed meant
// register/login were completely unusable in any topology with Redis disabled, and FailOpen (the
// obvious alternative) would have reopened exactly the unbounded-brute-force window FailClosed was
// built to prevent in the first place (see RateLimitFailureMode's own comment). This is the middle
// ground: while Redis can't answer, fall back to a per-PROCESS in-memory fixed-window counter -
// real throttling, just not shared across replicas or durable across a restart, instead of either
// extreme. Deliberately its own tiny class, not folded into RedisRateLimiter or RedisRateLimitFilter
// - it has no Redis dependency at all, so it can be constructed, tested, and reasoned about with
// zero network/mocking concerns, and RedisRateLimitFilter only ever reaches it on the one outcome
// (RedisUnavailable) where the Redis-backed limiter already gave up.
public interface ILocalFallbackRateLimiter
{
    bool TryAcquire(string keyPrefix, string partitionKey, int permitLimit, TimeSpan window);
}

public sealed class LocalFallbackRateLimiter : ILocalFallbackRateLimiter, IDisposable
{
    // One FixedWindowRateLimiter per (keyPrefix, partitionKey) - exactly the same partitioning
    // RedisRateLimiter's own Redis key ("ratelimit:{KeyPrefix}:{PartitionKey}") already uses, just
    // kept in-process instead of in Redis. permitLimit/window are read from whichever policy first
    // creates a given key's limiter and never change afterward - safe in practice since a
    // RateLimitPolicy is a singleton built once from configuration at startup, never from a value
    // that changes at runtime.
    //
    // Unbounded by partition key for the lifetime of the process - a known, accepted limitation
    // (same "good enough for this project's scope" category as EntityCacheService's per-replica L1
    // staleness or pgcat's ban_time tolerance), not something worth an eviction policy for a
    // fallback path that's only ever exercised while Redis itself is down. A real production
    // deployment exposing this to the public Internet would want a bound here (e.g. an LRU cap);
    // this sandbox's threat model (a single operator's own local stack) doesn't.
    private readonly ConcurrentDictionary<string, FixedWindowRateLimiter> _limiters = new();

    public bool TryAcquire(string keyPrefix, string partitionKey, int permitLimit, TimeSpan window)
    {
        var key = $"{keyPrefix}:{partitionKey}";
        var limiter = _limiters.GetOrAdd(key, _ => new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = window,
            QueueLimit = 0,
            AutoReplenishment = true,
        }));

        using var lease = limiter.AttemptAcquire();
        return lease.IsAcquired;
    }

    public void Dispose()
    {
        foreach (var limiter in _limiters.Values)
        {
            limiter.Dispose();
        }
    }
}
