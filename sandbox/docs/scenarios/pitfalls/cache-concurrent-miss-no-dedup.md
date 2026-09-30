# No de-duplication for concurrent cache misses

**Category:** best-practice **Status:** fixed

On a cache miss, every concurrent caller for the same key used to independently run
`fetchFromDb` and independently re-write the same cache entry — no in-flight lock/singleflight to
coalesce them. A burst of concurrent requests for the same just-expired key each hit Postgres
independently, instead of one request populating the cache for the rest — redundant DB load
proportional to concurrent request count on every cold key, worse the more popular the link.

✅ **Fix** — `GetOrFetch` (`src/backend/Shared/Common/EntityCacheService.cs`) now coalesces
concurrent misses on the same key through a `ConcurrentDictionary<string, Lazy<Task<T?>>>` of
in-flight fetches; joiners await the same `Lazy<Task<T?>>` instead of calling `fetchFromDb`
again, and the entry is removed once that fetch completes so a later, independent miss still
fetches fresh.

Worth knowing if this is ever demonstrated live: joiners share the first caller's
`CancellationToken`, so one caller cancelling cancels the fetch for everyone still waiting on it.

(see review-reports/2026-09-17-0153-3674b1b-linkapi-dbcontext-redis.md, F3)

## Relatives

### Nodes

- [Redis](node:redis-master)
- [LinkApi](node:link-api)
- [RedirectApi](node:redirect-api)

### Patterns

- [Caching](pattern:caching)
