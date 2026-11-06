# No de-duplication for concurrent cache misses

**Category:** concurrency **Status:** fixed

Watch for a cache-aside implementation with no coordination between concurrent callers that miss
on the *same* key at the *same* time — a classic cache-stampede/thundering-herd gap. Without an
in-flight lock or singleflight mechanism, every concurrent caller independently runs `fetchFromDb`
and re-writes the same entry: a burst of requests for a just-expired key each hits Postgres
independently instead of one populating the cache for the rest — redundant DB load proportional to
concurrent request count on every cold key, worse the more popular the link.

✅ **Do this instead** — `GetOrFetch` (`src/backend/Shared/Common/EntityCacheService.cs`) coalesces
concurrent misses on the same key through a `ConcurrentDictionary<string, Lazy<Task<T?>>>` of
in-flight fetches; joiners await the same `Lazy<Task<T?>>` instead of calling `fetchFromDb`
again, and the entry is removed once that fetch completes so a later, independent miss still
fetches fresh.

Worth knowing if this is ever demonstrated live: joiners share the first caller's
`CancellationToken`, so one caller cancelling cancels the fetch for everyone still waiting on it.

### A further refinement: removing the right `Lazy`, not just the right key

The coalescing fix above closed the double-fetch problem, but its own cleanup step had a narrower
follow-on race, found by a later review pass: `GetOrFetch`'s `finally` block did
`_inFlightFetches.TryRemove(key, out _)` — an *unconditional* remove that deletes whatever `Lazy`
currently sits at that key, not necessarily the one this call itself awaited. Under contention:

1. Caller A's fetch completes and starts its `finally`.
2. Before it runs, caller B's request for the same key (after a quick expire/re-miss) publishes a
   *third*, brand-new `Lazy` via its own `GetOrAdd`.
3. A's unconditional `TryRemove` deletes B's fresh entry — degrading single-flight coalescing back
   into independent DB fetches, under precisely the high-contention load this dictionary exists to
   protect against.

Correctness is unaffected either way (cache-aside is idempotent); only the stampede protection
degrades.

🐛 **Mistake** — `src/backend/Shared/Common/EntityCacheService.cs` (before `5c8475b`):

```csharp
finally
{
    _inFlightFetches.TryRemove(key, out _);
}
```

✅ **Do this instead** (`5c8475b`) — `ConcurrentDictionary<TKey,TValue>`'s conditional-remove
overload, which only removes the entry if it still equals both the key *and* this exact `Lazy`
reference (`Lazy<T>` has no overridden `Equals`, so this is reference equality on the specific
instance this call created or joined):

```csharp
finally
{
    // Conditional remove, not TryRemove(key, out _) - an unconditional remove deletes whatever
    // Lazy currently sits at this key, not specifically the one this call created/awaited. Two
    // callers racing the same key could otherwise have the first one's finally remove a THIRD
    // caller's brand-new Lazy (published via GetOrAdd right as the first one's fetch completed) -
    // degrading the single-flight coalescing this dictionary exists for back into N concurrent DB
    // fetches, under the exact load that makes coalescing matter most.
    _inFlightFetches.TryRemove(new KeyValuePair<string, Lazy<Task<T?>>>(key, lazyFetch));
}
```

(see `.notes/review-reports/2026-11-04-2129-close-5-bugs-round-2.md` — fixed in `5c8475b`, confirmed
by inspection rather than a failing-test reproduction)

## Relatives

### Nodes

- [Redis](node:redis-master)
- [LinkApi](node:link-api)
- [RedirectApi](node:redirect-api)

### Patterns

- [Caching](pattern:caching)
