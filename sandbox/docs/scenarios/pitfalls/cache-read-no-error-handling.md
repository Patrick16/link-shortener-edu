# Redis outage used to 500 every link read

**Category:** resilience-gap **Status:** fixed

A cache is an optimization, not a dependency — it should never be able to take down a read path
that has a working source of truth behind it. Wiring a cache client straight into the read path
with no fallback turns an availability problem that should be invisible (Redis blips, the whole
cache falls over) into a hard outage: if `GetCachedAsync` calls `IDistributedCache` directly with
no try/catch, any `RedisConnectionException` propagates as an unhandled exception instead of
falling back to the database, even though the database had the answer the whole time. Watch for
this any time a cache-aside layer is introduced — the degrade-on-failure path has to be designed in
from the start, not bolted on after the first outage.

⚠️ **Mistake** — `src/backend/Shared/Common/EntityCacheService.cs`, `GetCachedAsync`:

```csharp
public async Task<T?> GetCachedAsync(string id, CancellationToken cancellationToken)
{
    string? json = await _cache.GetStringAsync(Key(id), cancellationToken);
    return json is null ? null : JsonSerializer.Deserialize<T>(json, JsonOptions);
}
```

✅ **Do this instead** — wrap the call and degrade to a cache miss on failure (plus a
`Cache:Enabled` kill switch, used by the control panel's cache toggle):

```csharp
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
```

(fixed in `055b39c`)

## Relatives

### Nodes

- [Redis](node:redis-master) — the cache this bug affected
- [LinkApi](node:link-api) — calls `GetCachedAsync` on the read path
- [RedirectApi](node:redirect-api) — calls `GetCachedAsync` on the read path

### Patterns

- [Caching](pattern:caching) — this is a general risk of the cache-aside pattern, not specific
  to Redis: any store-agnostic caching layer needs the same fail-open behavior
