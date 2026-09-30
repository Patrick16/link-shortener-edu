# Redis outage used to 500 every link read

**Category:** logic-bug **Status:** fixed

A cache is an optimization, not a dependency — it should never be able to take down a read path
that has a working source of truth behind it. This one did: `GetCachedAsync` called
`IDistributedCache` directly with no try/catch, so any `RedisConnectionException` propagated as an
unhandled exception instead of falling back to Postgres, even though Postgres had the answer the
whole time.

🐛 **Bug** — `src/backend/Shared/Common/EntityCacheService.cs`, `GetCachedAsync`:

```csharp
public async Task<T?> GetCachedAsync(string id, CancellationToken cancellationToken)
{
    string? json = await _cache.GetStringAsync(Key(id), cancellationToken);
    return json is null ? null : JsonSerializer.Deserialize<T>(json, JsonOptions);
}
```

✅ **Fix** — wraps the call and degrades to a cache miss on failure (plus a `Cache:Enabled` kill
switch, used by the control panel's cache toggle):

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

(see review-reports/2026-09-17-0153-3674b1b-linkapi-dbcontext-redis.md, F1 — fixed in `055b39c`)

## Relatives

### Nodes

- [Redis](node:redis-master) — the cache this bug affected
- [LinkApi](node:link-api) — calls `GetCachedAsync` on the read path
- [RedirectApi](node:redirect-api) — calls `GetCachedAsync` on the read path

### Patterns

- [Caching](pattern:caching) — this is a general risk of the cache-aside pattern, not specific
  to Redis: any store-agnostic caching layer needs the same fail-open behavior
