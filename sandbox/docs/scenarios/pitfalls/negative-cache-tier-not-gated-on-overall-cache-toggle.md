# A new cache tier can forget to honor the flag that's supposed to disable every tier

**Category:** architecture-bug **Status:** fixed

Watch for this any time a cache gains a new tier or a new kind of entry (a second TTL, a negative
marker, an L1 layer ahead of the shared store) on top of an existing "disable caching entirely"
toggle. Each new tier tends to get written against its *own* feature flag only, because that's the
flag the new code is actually about — it's easy to forget that the pre-existing "disable everything"
flag is supposed to still win over every tier, old and new alike, or a demo/toggle that's supposed
to mean "every read now goes to the database" quietly stops being true for whatever the newest
tier added.

**Concrete instance in this project:** `EntityCacheService<T>` already had `Cache:Enabled` (the
"disable all caching" toggle) and, from an earlier feature, `Cache:L1Enabled` — and the positive-L1
write path (`PopulateL1`) was already correctly double-gated on both flags together. When negative
caching (`Cache:NegativeEnabled`) was added, its own L1 write/read were gated only on
`_negativeCacheEnabled`, not also on `_cacheEnabled` — so flipping the overall `Cache:Enabled`
toggle off (the control panel's "every read goes to the DB" demo) would still leave negatively
cached keys serving from L1, an inconsistency with what the toggle is supposed to guarantee.

⚠️ **Mistake** — reconstructed from the commit's own description; both the bug and its fix landed
in the same commit before anything was pushed, so there is no separate buggy commit to diff against
(`src/backend/Shared/Common/EntityCacheService.cs`):

```csharp
private async Task<bool> IsNegativelyCachedAsync(string id, CancellationToken cancellationToken)
{
    if (!_negativeCacheEnabled) return false; // only checks its own flag

    if (_l1Cache.TryGetValue(NegativeKey(id), out bool _)) return true;
    // ... Redis read ...
}
```

✅ **Do this instead** (`b0097d2`) — gate on both flags together, matching the pattern the
pre-existing positive-L1 path (`PopulateL1`) already used:

```csharp
private async Task<bool> IsNegativelyCachedAsync(string id, CancellationToken cancellationToken)
{
    // Both flags checked together - Cache:Enabled=false must mean no layer caches anything
    // (positive or negative), or the "every read goes to the DB" demo would be lying for a
    // negatively-cached key.
    if (!_cacheEnabled || !_negativeCacheEnabled) return false;

    if (_l1Cache.TryGetValue(NegativeKey(id), out bool _)) return true;
    // ... Redis read ...
}
```

`CacheNegativeAsync` (the write side) got the same `!_cacheEnabled || !_negativeCacheEnabled` guard.

**A second, smaller correctness slip in the same commit:** the negative marker's read originally
checked `marker is not null`. Against real Redis this is harmless (an absent key genuinely returns
`null`), but an unconfigured `Mock<IDistributedCache>.GetAsync` in a loose test setup defaults to a
non-null, empty `byte[]`, which decodes to `""`, not `null` — so the check was imprecise either way,
since the marker is always written as the literal `"1"`. Tightened to `!string.IsNullOrEmpty(marker)`
before this ever shipped. Worth knowing as a general pattern for sentinel-value checks against
`IDistributedCache` (`GetStringAsync` returning `null` means "absent," never `""`), even though this
specific case never manifested against a real Redis instance.

(fixed in `b0097d2` — found while writing tests for the feature, before the commit ever landed; see
`.notes/feature-docs/2026-10-27-1313-negative-caching.md`)

## Relatives

### Nodes

- [LinkApi](node:link-api)
- [RedirectApi](node:redirect-api)
- [Redis](node:redis-master)

### Patterns

- [Caching](pattern:caching)
