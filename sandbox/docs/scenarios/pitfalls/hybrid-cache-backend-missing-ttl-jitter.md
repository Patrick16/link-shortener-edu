# A second cache-backend implementation quietly dropped a jitter detail the first one got right

**Category:** performance **Status:** fixed

Watch for this any time a codebase grows a second implementation of an interface that already has
a working, battle-tested implementation — especially when the new one is built on a library instead
of hand-rolled, and its author (quite reasonably) treats "match the public contract" as the bar for
parity. A TTL jitter spread is exactly the kind of detail that's easy to drop silently: it changes
nothing about correctness, there's no compiler error, no failing test (unless a test specifically
asserts on the TTL distribution), and the new implementation "works" in every manual check. The gap
only shows up as a timing correlation under real concurrent load — the one scenario a quick manual
test of a cache backend is least likely to reproduce.

**Concrete instance in this project:** `EntityCacheService<T>` (the original, hand-rolled Redis+L1
cache) deliberately jitters every TTL by ±10% (`ApplyTtlJitter`) specifically so a burst of
same-time writes doesn't all expire in the same one-second window later, which would otherwise
produce a synchronized wave of cache misses hitting Postgres all at once. When
`HybridEntityCacheService<T>` was built as a second, swappable backend on top of the BCL's
`Microsoft.Extensions.Caching.Hybrid`, its own class comment carefully enumerated every other
behavioral gap from the original (no negative-cache TTL, no circuit breaker, no L1/negative
sub-toggles) — but TTL jitter wasn't mentioned at all, because it wasn't on the interface and wasn't
something the author was deliberately scoping out; it was just missing. Every one of this class's 7
new unit tests passed regardless, since none of them asserted anything about TTL variance. The gap
was only caught by a dedicated review pass reasoning about "what would a burst of same-time writes
do under this backend," not by running anything.

⚠️ **Mistake** — `src/backend/Shared/Common/HybridEntityCacheService.cs` (as shipped in `19bcf7a`):

```csharp
private const int DefaultTtlSeconds = 3600;

// ...

private static HybridCacheEntryOptions EntryOptions(int ttlSeconds) =>
    new() { Expiration = TimeSpan.FromSeconds(ttlSeconds) };
```

Every entry written through this backend — including `ShortenerService`'s `LinkCreatedConsumer`
pre-warming the cache on every link it persists — gets the exact same TTL relative to its own write
time, so a burst of link creations in the same minute all expire in the same one-second window an
hour later, reproducing precisely the correlated DB-miss spike the original backend's jitter exists
to avoid.

✅ **Do this instead** (`abe36ea`) — port the identical ±10% spread the original backend already
uses, rather than re-deriving a new jitter formula or constant:

```csharp
// Same +-10% spread as EntityCacheService.ApplyTtlJitter, for the same reason (see that
// method's own comment): without it, a burst of same-time writes - a k6 load test, or
// ShortenerService pre-warming the cache on a batch consume - would all expire in lockstep and
// reproduce the exact synchronized DB-miss spike jitter exists to avoid. Found missing by
// review (.notes/review-reports/2026-11-05-1420-hybridcache-backend-toggle.md, F1).
private const double TtlJitterFraction = 0.1;

private static HybridCacheEntryOptions EntryOptions(int ttlSeconds) =>
    new() { Expiration = TimeSpan.FromSeconds(ApplyTtlJitter(ttlSeconds)) };

// Random.Shared, not a seeded/injected instance - same reasoning as EntityCacheService's own
// copy of this method: a fixed seed would defeat the entire point of decorrelating
// concurrently-created entries.
private static double ApplyTtlJitter(int ttlSeconds) =>
    ttlSeconds * (1 + ((Random.Shared.NextDouble() * 2) - 1) * TtlJitterFraction);
```

The reusable lesson: when a second implementation of an already-hardened interface exists, diff its
*non-functional* behavior against the original too (timing distributions, retry/backoff shape,
failure-mode handling), not just its functional contract — a parity gap in those dimensions produces
no test failure and no wrong answer, only a load-shape regression that's easy to miss until it's
live.

(see `.notes/review-reports/2026-11-05-1420-hybridcache-backend-toggle.md`, F1 — fixed in `abe36ea`)

## Relatives

### Nodes

- [LinkApi](node:link-api)
- [RedirectApi](node:redirect-api)
- [ShortenerService](node:shortener-service) — the consumer most likely to trigger the correlated
  burst this jitter prevents, via `LinkCreatedConsumer`'s cache pre-warm

### Patterns

- [Caching](pattern:caching)
