## What it is

Cache-aside (a.k.a. lazy-loading) caching: an application checks a fast in-memory store before
querying its actual database, and populates that store on a miss. See
[Redis's own intro](https://redis.io/docs/latest/develop/get-started/) for the general concept —
this doc covers the pattern, not any one product.

## What it solves

A relational database is a poor fit for read paths that are hit far more often than they change.
Scenario 1's redirect path (`GET /{hash}`) is the clearest example in this project: a link is
written once and then read every time someone clicks it — potentially thousands of times — so
routing every read through Postgres wastes capacity on work whose answer almost never changes.

## How it works

1. On a read, check the cache first (keyed by whatever identifies the entity — here, the link's
   hash).
2. **Hit:** return the cached value, skip the database entirely.
3. **Miss:** query the database, then write the result into the cache (usually with a TTL) before
   returning it, so the *next* read for the same key is a hit.
4. On a write that changes the underlying data, invalidate (or update) the corresponding cache
   entry — otherwise readers keep seeing the stale cached value until its TTL expires.

The trade-off this pattern always carries: the cache can now be wrong for a window (write landed
in the DB, cache invalidation hasn't happened or hasn't propagated yet) — see the Pitfalls below
for where that shows up concretely in this codebase, and step 4 above for the mechanism that's
supposed to keep the window short.

**A cache must never be a second source of truth.** Whatever backs it (Redis here) should be
treated as disposable — every entry is reconstructible from the database, so losing the whole
cache is a performance blip, never a correctness problem. This is also why cache failures should
degrade to "go to the database," never propagate as an error — see
[a real instance of this project getting that wrong before fixing it](pitfall:cache-read-no-error-handling).

## How it's implemented here

Generic across any cached entity via `EntityCacheService<T>` (`src/backend/Shared/Common/
EntityCacheService.cs`) — `GetOrFetch(id, fetchFromDb, ct)` is steps 1-3 above in one call.
[Redis](node:redis-master) is the concrete store; see that node's doc for the actual key format and the
two real bugs found in this exact code path.

## Pitfalls

- 🐛 [Redis outage used to 500 every link read](pitfall:cache-read-no-error-handling)
  (logic-bug) — fixed
- 🐛 [No de-duplication for concurrent cache misses](pitfall:cache-concurrent-miss-no-dedup)
  (best-practice) — fixed
- 🐛 [Shared `Common` library pulled in the full Redis client just for one interface](pitfall:common-lib-full-redis-client-dependency)
  (best-practice) — fixed

## Relatives

### Nodes

- [Redis](node:redis-master)
- [LinkApi](node:link-api)
- [RedirectApi](node:redirect-api)

### Patterns

None yet.
