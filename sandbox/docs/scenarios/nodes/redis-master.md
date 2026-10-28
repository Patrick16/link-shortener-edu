## What it is

[Redis](https://redis.io/docs/latest/) — an in-memory key-value store, fronting Postgres for both
LinkApi's and RedirectApi's hot reads. This is the master of a master + 2 replicas + 3 Sentinels
topology (see the HA note under Relatives) — for the caching role covered here, treat it as one
logical Redis; the replica/Sentinel setup is a separate topic (availability, not caching).

## What it solves

See [Caching](pattern:caching) for the general problem this addresses.

## How it works

See [Caching](pattern:caching) — the mechanism here is the standard cache-aside pattern, nothing
Redis-specific beyond using it as the store.

## How it's implemented here

Both [LinkApi](node:link-api) and [RedirectApi](node:redirect-api) share one cache-key format
(`link:{hash}`) through `LinkCacheService`, so a link created by one is served by the other without
a second cache population — this sharing is *why* `LinkCacheService` was pulled out of LinkApi into
`Shared/Common` rather than living in either service:

```csharp
// src/backend/Shared/Common/LinkCacheService.cs:14
protected override string Key(string id) => $"link:{id}";
```

Reads go through `EntityCacheService<T>.GetOrFetch` (`src/backend/Shared/Common/
EntityCacheService.cs:79`) — check cache, fall back to `fetchFromDb` on a miss, populate the cache
with what was fetched.

## Standing toggle

A "Redis" row in the architecture-map UI's Topology panel starts/stops this container and moves
`CacheEnabled` with it **in both directions** — unlike most other standing toggles, where a parent
forces a dependent off but restoring the parent never auto-restores the dependent. A `redis-master`
nobody's allowed to read from would be a node with no reason to exist, so there's deliberately no
"up but unusable" state reachable through this control. Turning it OFF also force-disables the
nested [Redis Sentinel (HA)](node:redis-sentinel-1) toggle (nothing left to fail over to); turning
it back ON does **not** auto-restore Sentinel — that stays its own explicit action. Equivalent API:
`POST /api/infra/redis {"enabled": bool}`.

## Pitfalls

- 🐛 [Redis outage used to 500 every link read](pitfall:cache-read-no-error-handling)
  (resilience-gap) — fixed
- 🐛 [No de-duplication for concurrent cache misses](pitfall:cache-concurrent-miss-no-dedup)
  (concurrency) — fixed
- 🐛 [redis-master accepted writes with no fencing against a split brain](pitfall:redis-master-no-split-brain-fencing)
  (architecture-bug) — fixed
- 🐛 [Only the Redis master asked Sentinel who's in charge before rejoining - the replicas didn't](pitfall:redis-replica-ignores-sentinel-on-rejoin)
  (architecture-bug) — fixed
- 🐛 [Shared `Common` library pulled in the full Redis client just for one interface](pitfall:common-lib-full-redis-client-dependency)
  (coupling) — fixed
- ⚠️ [A Redis blip at boot crashed the whole API](pitfall:redis-multiplexer-abort-on-connect-fail-crashes-host)
  (resilience-gap) — fixed
- ⚠️ [Fail-open that only caught RedisException let timeouts fail the redirect](pitfall:fail-open-catch-too-narrow)
  (resilience-gap) — fixed
- ⚠️ [A CLI flag baked in on every container start can quietly undo a runtime CONFIG SET](pitfall:redis-min-replicas-to-write-not-resynced-on-restart)
  (resilience-gap) — fixed

## Relatives

### Nodes

- [LinkApi](node:link-api) — writes/reads through the same `link:{hash}` key
- [RedirectApi](node:redirect-api) — writes/reads through the same `link:{hash}` key
- [Redis Sentinel 1](node:redis-sentinel-1) — monitors this node, promotes a replica if it dies

### Patterns

- [Caching](pattern:caching)
- [High availability & failover](pattern:high-availability-failover)
- [Circuit breakers](pattern:circuit-breakers) — a Polly breaker sits in front of every call this
  node's clients make
- [Distributed locking: Redis claims](pattern:distributed-locking-redis) — the coordinator behind
  `TopLinksPushService`'s cross-replica push claim
