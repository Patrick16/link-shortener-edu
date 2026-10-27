# A second cache tier ahead of a shared cache reintroduces the staleness problem the shared one solved

**Category:** race-condition **Status:** known limitation

Watch for this any time a service gets scaled to multiple replicas *after* an in-process ("L1")
cache has been added ahead of a shared one ("L2", e.g. Redis). A shared cache is, by construction,
the same answer for every replica — invalidate it once and every replica sees the new value on its
next read. An in-process cache has no such guarantee: each replica's copy lives in that replica's
own memory, and invalidating it on the replica that happened to handle the write does nothing for
every *other* replica's copy of the same key.

**Concrete instance in this project:** `EntityCacheService<T>`'s L1 tier
(`Microsoft.Extensions.Caching.Memory.IMemoryCache`, checked before Redis on every
`GetOrFetch`) is local to each process. `InvalidateCacheAsync` evicts the local L1 entry
unconditionally, in addition to the pre-existing Redis `RemoveAsync` — but only on whichever
replica's process actually ran the invalidation. With `link-api`/`redirect-api` scaled to 2+
replicas, a delete or update that lands on replica A clears Redis and replica A's own L1 copy;
replica B's L1 copy of that same key is untouched and keeps serving the pre-delete/update value
until its own local TTL expires. A request that happens to land on replica B within that window
sees a link that was just deleted, or the link's old destination, even though the authoritative
(Redis/Postgres) state has already moved on.

**Why no fix was built:** closing this would need a cross-process invalidation channel (Redis
pub/sub broadcasting "evict key X" to every replica's L1, or similar) — a real subscriber cost paid
by every process, for every key, to shrink a window that's already short by construction: L1's TTL
is a fixed 2 seconds, deliberately short specifically so this window stays small and observable
rather than durable. The project's own standing position (see [Caching](pattern:caching)'s "What it
works" section) is that a cache is disposable and reconstructible, never a second source of truth —
a 2-second-wide staleness window on a disposable cache is accepted as the cost of the latency win
L1 buys, not treated as a correctness bug to close.

**How to see it:** scale `link-api` or `redirect-api` to 2+ replicas, read the same link repeatedly
through the load balancer until it's warm in more than one replica's L1, delete or update that link
(landing on one specific replica), then read it again immediately — a response routed to a
different replica can still serve the pre-delete/update value for up to 2 seconds.

(not fixed — accepted tradeoff, see `.notes/feature-docs/2026-10-27-1231-l1-in-process-cache.md`)

## Relatives

### Nodes

- [LinkApi](node:link-api)
- [RedirectApi](node:redirect-api)
- [Redis](node:redis-master) — the L2 tier this L1 sits ahead of, and the one tier that *is* shared
  across replicas

### Patterns

- [Caching](pattern:caching)
