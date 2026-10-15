# A fail-open tombstone write is not enough on its own to invalidate an already-cached entity

**Category:** resilience-gap
**Status:** fixed

A cache-aside entry and a separate "this key is dead" marker look like they cover each other, but
only if *both* of them are written reliably — and a marker that's deliberately fail-open (so a
transient cache outage can't itself break the read path) is, by definition, not reliable. If the
only thing standing between a stale cache hit and the correct answer is that one fail-open write
succeeding, a cache outage at exactly the wrong moment turns a liveness problem (cache miss, fall
back to the database) into a silent correctness problem (cache hit, serve a wrong answer) with no
second line of defense.

**Concrete instance in this project:** before this fix, `RedirectApi`'s read path had two checks
feeding into whether a link resolves: the `link:deleted:{hash}` tombstone (checked first) and a
live `DeletedAt`/`ExpiresAt` check on the `Link` row the main `link:{hash}` cache-aside entry
returns. The live check only actually runs any work on a cache *miss* — on a cache *hit*, `link` is
whatever was cached at write time, which for `ExpiresAt` is fine (it's immutable from creation,
checking a stale copy against `now()` still works) but for `DeletedAt` is not: a `Link` cached
*before* being deleted has `DeletedAt == null` baked in forever, regardless of what Postgres says
now. The only thing that could catch that was the tombstone — and `RedisLinkDeletionMarkerStore`
fails open (logs a warning, returns/continues) on any Redis error, by design, so the read path
keeps working through a Redis hiccup. That same design meant a tombstone-confirm failure during,
say, a brief Redis Sentinel failover window left an already-cached, now-deleted link resolving
successfully for up to the main cache entry's full remaining TTL (up to an hour by default) — not
the 60-second window the tombstone's own TTL would suggest.

⚠️ **Mistake** — `src/backend/Services/ShortenerService/LinkDeletedConsumer.cs` and
`LinkExpiryWorker.cs` (before `437bd19`) — only the tombstone was confirmed, the main cache entry
was never touched:
```csharp
// LinkDeletedConsumer.HandleBatchAsync
foreach (var hash in existingHashes)
{
    await _deletionMarkers.ConfirmAsync(hash, DeletedReason.Manual, now, cancellationToken);
}
```
```csharp
// LinkExpiryWorker.ExecuteAsync
var now = DateTime.UtcNow;
foreach (var hash in expiredHashes)
{
    await _deletionMarkers.ConfirmAsync(hash, DeletedReason.Expired, now, cancellationToken);
}
```

✅ **Do this instead** — invalidate the main cache-aside entry alongside the tombstone confirm, in
both places, so either write succeeding independently is enough to close the gap:
```csharp
// LinkDeletedConsumer.HandleBatchAsync
foreach (var hash in existingHashes)
{
    await _deletionMarkers.ConfirmAsync(hash, DeletedReason.Manual, now, cancellationToken);
    await _cache.InvalidateCacheAsync(hash);
}
```
```csharp
// LinkExpiryWorker.ExecuteAsync
var now = DateTime.UtcNow;
foreach (var hash in expiredHashes)
{
    await _deletionMarkers.ConfirmAsync(hash, DeletedReason.Expired, now, cancellationToken);
    await _cache.InvalidateCacheAsync(hash);
}
```

The two writes are still independent, not transactional with each other — this is deliberate
defense-in-depth, not a half-finished fix. Before this feature, a cached `Link` was "create once,
correct forever" (nothing about an existing row ever changed after being cached), so cache
staleness was purely a liveness question. This feature is the first time a cached entity's
correctness can silently change *after* being cached — worth watching for in any system where a
cache-aside entity gains a field that can flip after the entity was already cached.

(see `.notes/review-reports/2026-10-15-1437-link-deletion-and-expiration.md`, F2 — fixed in `437bd19`)

## Relatives

### Nodes

- [ShortenerService](node:shortener-service) — where the missing invalidation lived (both the
  manual-delete consumer and the TTL sweep worker)
- [RedirectApi](node:redirect-api) — the read path whose cache-hit branch never re-checks `DeletedAt`
- [Redis](node:redis-master) — hosts both the tombstone key family and the main cache-aside entry

### Patterns

- [Caching](pattern:caching) — the cache-aside mechanism this gap is specific to
