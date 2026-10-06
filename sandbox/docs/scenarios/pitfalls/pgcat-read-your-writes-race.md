# A read immediately after a write can land on a lagging replica

**Category:** race-condition **Status:** fixed

PgCat's auto-routing treats every `SELECT` the same way regardless of what just happened on the
same connection, the same HTTP request, or even the same millisecond — it can send a read to a
replica that hasn't yet streamed the write that same logical operation just made. Streaming
replication lag is normally small, but it is never zero, and under load (or a momentarily slow
replica) the window is wide enough to matter.

**Concrete failure scenario in this project:** create a link (`POST /Links`, returns synchronously
once `LinkApi` has generated the hash and published the event — see
[Async messaging](pattern:async-messaging)), then immediately resolve it
(`GET /{hash}` on `RedirectApi`). If the read lands on a replica that hasn't caught up yet — or
even on the primary, before `ShortenerService` has finished consuming the event and persisting the
row — the lookup 404s even though the link "exists" from the caller's point of view. This is the
same shape as the pre-existing create→resolve eventual-consistency gap the async publish/consume
boundary already has, compounded by replica lag being a second source of the same kind of delay.

The usual fixes (routing a read-after-write to the primary for some window, session-level
consistency tokens, synchronous replication for affected queries) all give back some of what
read-replica routing was built to gain in the first place, and — more importantly here — only
address the replica-lag half of the race, not the async publish/consume half: the row may not
exist in *any* Postgres instance, primary included, until `ShortenerService` consumes the event.

✅ **The fix:** `LinkApi.CreateLink` writes the just-created `Link` straight into Redis itself
(`LinksController.cs`, `OptimisticCacheTtlSeconds` — 60s), the same cache `RedirectApi` already
checks first before ever touching Postgres (see [Caching](pattern:caching)). `ShortenerService`'s
own consumer already overwrote this same cache key with the full TTL once it actually persisted
the row ([pre-existing behavior](node:shortener-service)) — plain last-write-wins on one key, no
new coordination. Since the cache check happens before any Postgres read, this closes *both*
sources of the race for the TTL window, not just the replica-lag one. No negative caching happens
here (a cache miss still falls through to Postgres and returns an honest 404 if the row genuinely
isn't there), so a persist that never completes just expires back to the real behavior after 60s —
no explicit invalidation needed. The existing cache-disable control-panel toggle doubles as a way
to demonstrate the original race on purpose: turn caching off and this fix no longer applies.

**A second, pre-existing bug was blocking this fix and was found only by verifying it live:** the
fix above looked correct by every static check and still 404ed on a real create→immediate-redirect
request, because `LinkApi`/`RedirectApi`/`ShortenerService` each had their own `Redis:InstanceName`,
so their "shared" cache entries landed in three different physical Redis keyspaces — see
[Three services ran identical caching code against three disjoint Redis keyspaces](pitfall:redis-instance-name-breaks-cross-service-cache-sharing)
for the full story. That bug predates this fix and also broke `ShortenerService`'s own pre-warm;
fixing it was a precondition for this one actually working, not an optional cleanup alongside it.

## Relatives

### Nodes

- [PgCat](node:pgcat)
- [LinkApi](node:link-api) — writes the optimistic cache entry
- [RedirectApi](node:redirect-api) — cache-first read that the fix relies on
- [ShortenerService](node:shortener-service) — overwrites the same key with the full TTL once persisted

### Patterns

- [Read-replica routing](pattern:read-replica-routing)
- [Caching](pattern:caching)

### Pitfalls

- [Three services ran identical caching code against three disjoint Redis keyspaces](pitfall:redis-instance-name-breaks-cross-service-cache-sharing)
  — the precondition bug found while verifying this fix
- [Caching](pattern:caching)
