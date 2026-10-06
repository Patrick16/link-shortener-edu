# A side-effect cache failure took down an entire gRPC request, not just the caching optimization

**Category:** resilience-gap **Status:** fixed

A cache sitting behind a side effect (not the main thing being requested, just an optimization on
top of it) needs the same fail-open treatment as a cache sitting directly on a read path — but
it's easy to forget that lesson the second time around, once the cache lives inside a new wrapper
class instead of the one that already got this treatment. The failure mode is worse than a plain
read-path cache miss too: if the surrounding call is itself on a synchronous, user-facing request
path, an uncaught exception from the cache doesn't just mean "go fetch it the slow way" — it fails
the entire request, even though the actual work the caller cares about already succeeded.

**Concrete failure scenario in this project, found live, not during code review:**
`CachingGeoIpResolver.ResolveAsync` wraps a geo-IP lookup — a pure enrichment detail of a click,
unrelated to whether the click itself was recorded — in a Redis-backed cache. `EntityCacheService`
(the equivalent wrapper for links) already guards its own cache reads/writes with try/catch for
exactly this reason, but `CachingGeoIpResolver` was written later and didn't get the same
treatment. In the default async/RabbitMQ consumer path this was low-severity: a Redis exception
on the cache write would fail that one message's processing and get redelivered, invisible to the
end user (the redirect had already returned before this code runs). But toggling the stack to
messaging-mode=grpc puts the exact same `TrafficService.ClickTrackedConsumer` code inside
`MessagingGrpcServiceBase.Publish`, on the synchronous call chain backing the user's own redirect
request. Live-testing that mode surfaced a real `StackExchange.Redis.RedisServerException:
NOREPLICAS Not enough good replicas to write` from the cache write — the click row had already been
committed to `clicks_db` (confirmed via direct `SELECT`), but the uncaught exception turned into
`RpcException(Unknown, "Exception was thrown by handler.")`, which `GlobalExceptionHandler`
correctly mapped to a 502 — "correct" handling of an exception that a pure caching optimization
should never have let escape in the first place.

⚠️ **Mistake** — reconstructed; no diffable pre-fix commit exists (this feature was reviewed and
fixed before its first commit — see
`.notes/review-reports/2026-10-06-1430-messaging-toggle-grpc-review.md`, F11):

```csharp
public async Task<GeoLocation> ResolveAsync(string? ipAddress, CancellationToken cancellationToken = default)
{
    if (string.IsNullOrEmpty(ipAddress))
    {
        return await inner.ResolveAsync(ipAddress, cancellationToken);
    }

    var key = $"geoip:{ipAddress}";
    var cached = await cache.GetStringAsync(key, cancellationToken);
    if (cached is not null)
    {
        return JsonSerializer.Deserialize<GeoLocation>(cached);
    }

    var resolved = await inner.ResolveAsync(ipAddress, cancellationToken);
    await cache.SetStringAsync(key, JsonSerializer.Serialize(resolved), CacheOptions, cancellationToken);
    return resolved;
}
```

✅ **Do this instead** — `src/backend/Shared/Infrastructure/CachingGeoIpResolver.cs`: wrap both the
cache read and the cache write independently, log a warning, and fall back to "treat as a cache
miss" / "continue without caching" — the same pattern `EntityCacheService` already used:

```csharp
public async Task<GeoLocation> ResolveAsync(string? ipAddress, CancellationToken cancellationToken = default)
{
    if (string.IsNullOrEmpty(ipAddress))
    {
        return await inner.ResolveAsync(ipAddress, cancellationToken);
    }

    var key = $"geoip:{ipAddress}";

    try
    {
        var cached = await cache.GetStringAsync(key, cancellationToken);
        if (cached is not null)
        {
            return JsonSerializer.Deserialize<GeoLocation>(cached);
        }
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Failed to read geo-IP cache for {Key} - treating as a cache miss", key);
    }

    var resolved = await inner.ResolveAsync(ipAddress, cancellationToken);

    try
    {
        await cache.SetStringAsync(key, JsonSerializer.Serialize(resolved), CacheOptions, cancellationToken);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Failed to populate geo-IP cache for {Key} - continuing without it", key);
    }

    return resolved;
}
```

Verified live after the fix: the exact same create-link-then-redirect sequence that previously
returned a 502 now returns a 302 — the underlying Redis `NOREPLICAS` condition recurred (it's a
real, repeatable flakiness in this project's Redis Sentinel replication, not a one-off), but now
only logs a warning instead of failing the request.

(see `.notes/review-reports/2026-10-06-1430-messaging-toggle-grpc-review.md`, F11 — fixed; no
pre-fix commit hash cited, reconstructed from the finding's own description plus the current code
at HEAD)

## Relatives

### Nodes

- [TrafficService](node:traffic-service) — where this surfaced live, inside `ClickTrackedConsumer`
- [Redis](node:redis-master) — the cache (and the Sentinel replication flakiness that triggered this)

### Patterns

- [Caching](pattern:caching) — same "a cache must never be able to break its caller" lesson as
  [Redis outage used to 500 every link read](pitfall:cache-read-no-error-handling), applied to a
  different cache
- [Async messaging](pattern:async-messaging) — only became user-visible because the sync gRPC
  dispatch mode puts a consumer's code on the request's own call chain
