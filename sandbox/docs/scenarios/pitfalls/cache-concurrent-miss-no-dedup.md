# No de-duplication for concurrent cache misses

**Category:** concurrency **Status:** fixed

Watch for a cache-aside implementation with no coordination between concurrent callers that miss
on the *same* key at the *same* time — a classic cache-stampede/thundering-herd gap. Without an
in-flight lock or singleflight mechanism, every concurrent caller for the same key independently
runs `fetchFromDb` and independently re-writes the same cache entry. A burst of concurrent requests
for the same just-expired key each hits Postgres independently, instead of one request populating
the cache for the rest — redundant DB load proportional to concurrent request count on every cold
key, worse the more popular the link.

✅ **Do this instead** — `GetOrFetch` (`src/backend/Shared/Common/EntityCacheService.cs`) coalesces
concurrent misses on the same key through a `ConcurrentDictionary<string, Lazy<Task<T?>>>` of
in-flight fetches; joiners await the same `Lazy<Task<T?>>` instead of calling `fetchFromDb`
again, and the entry is removed once that fetch completes so a later, independent miss still
fetches fresh.

Worth knowing if this is ever demonstrated live: joiners share the first caller's
`CancellationToken`, so one caller cancelling cancels the fetch for everyone still waiting on it.

## Relatives

### Nodes

- [Redis](node:redis-master)
- [LinkApi](node:link-api)
- [RedirectApi](node:redirect-api)

### Patterns

- [Caching](pattern:caching)
