## What it is

Redis's atomic conditional-set: `SET key value NX PX ttl` — set the key only if it doesn't already
exist (`NX`), with an expiry in milliseconds (`PX`). Redis executes commands single-threaded, so
the "check it doesn't exist, then set it" happens as one indivisible server-side step — there's no
race window between two replicas' check and set the way there would be with a naive `GET` then
`SET`. StackExchange.Redis exposes this as `IDatabase.StringSetAsync(key, value, ttl,
When.NotExists)`.

**Honestly, this is not "Redlock."** Redis's own documented algorithm for a genuinely
safety-critical distributed lock runs against an odd number of *independent* Redis instances and
requires acquiring a majority of them within a tight clock budget. This project runs one Redis
primary — a single point of failure for the lock exactly as much as it already is for caching (see
[High availability & failover](pattern:high-availability-failover) for the Sentinel setup that
exists for the *data*, not for lock semantics specifically). That's an accepted trade-off here
because what this lock protects — see "What it solves" — is a bounded, low-stakes deduplication,
not something where a double-execution would be a real correctness bug.

## What it solves

See [Distributed locking](pattern:distributed-locking) for the general problem. This project uses
the same `SET NX PX` primitive two different ways:

- **One-shot claim** (`TopLinksPushService`) — a **short-lived, TTL-scoped claim** that needs no
  dedicated connection and works fine through any pooled Redis client; the trade-off for not
  needing a pinned connection is that the lock's lifetime is a number you have to pick up front,
  not something tied to a connection's own liveness (contrast with
  [the Postgres advisory-lock implementation](pattern:distributed-locking-postgres-advisory)).
- **Sustained lease** (`LinkExpiryWorker`) — the same primitive, but claimed once and then
  **renewed** on a heartbeat for as long as the holder wants to stay leader, instead of letting it
  expire after one short critical section. See "Sustained lease: `LinkExpiryWorker`'s leader
  election" below.

## How it works

```mermaid
sequenceDiagram
    participant A as Replica A
    participant R as Redis
    participant B as Replica B

    A->>R: SET push-claim:user NX PX 2000
    R-->>A: OK - acquired
    B->>R: SET push-claim:user NX PX 2000
    R-->>B: refused - key already exists
    Note over A: still working past the 2s TTL (e.g. a slow query)
    R->>R: TTL expires, key auto-deleted
    Note over B: B's own scheduled retry happens to land here
    B->>R: SET push-claim:user NX PX 2000
    R-->>B: OK - acquired, even though A may still be mid-flight
```

That last step is the TTL implementation's known, accepted risk: if the holder's work legitimately
outlives the TTL, a second replica can acquire the same claim while the first is technically still
working — unlike the session-scoped Postgres lock, there's no live connection whose death the
coordinator can observe, only a clock. The TTL here is deliberately set to the same number the
caller would have throttled itself to locally anyway (see below), so an early release means "a
little more freshness than strictly necessary," not "two replicas doing conflicting writes."

**The failure-mode choice that matters more than the mechanism**: what happens when the *claim
call itself* fails (Redis unreachable)? This project answers that per call site based on the
stakes, not as a library default:

```csharp
// src/backend/Services/ReportingApi/Realtime/TopLinksPushService.cs:147-159
private async Task<bool> TryClaimPushAsync(Guid userId, CancellationToken cancellationToken)
{
    try
    {
        var db = _connectionMultiplexer.GetDatabase();
        return await db.StringSetAsync(PushClaimKey(userId), "1", MinPushInterval, When.NotExists);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        _logger.LogWarning(ex, "Failed to claim push for user {UserId} - proceeding without cluster-wide dedup for this tick", userId);
        return true; // fail OPEN
    }
}
```

Failing **open** here (treat a Redis error as "claim granted") means a Redis outage degrades back
to every replica pushing independently — this service's own pre-backplane behavior, and "a
dashboard might see one duplicate update" is strictly better than "no replica pushes anything at
all." A different call site with higher stakes makes the opposite call with the exact same
primitive: `AuthApi`'s Redis-backed rate limiter
(`src/backend/Shared/WebDefaults/RateLimitPolicy.cs:14-15`)
fails **closed** instead — an open window during a Redis outage on register/login would mean
unlimited brute-force attempts, so there "never block the hot path on a dependency outage" is the
wrong default. Same atomic claim, opposite failure-mode decision, chosen per call site by what a
false "proceed" would actually cost.

## How it's implemented here

`TopLinksPushService.PushAsync`
(`src/backend/Services/ReportingApi/Realtime/TopLinksPushService.cs:96-140`)
takes the cluster-wide claim before doing any real work — querying ClickHouse or sending to the
SignalR group — so a replica that loses the race does neither:

```csharp
// src/backend/Services/ReportingApi/Realtime/TopLinksPushService.cs:108-111
if (!await TryClaimPushAsync(userId, cancellationToken))
{
    return;
}
```

The TTL is `MinPushInterval` (2 seconds) — not an arbitrary separate number, but literally the same
interval this push would have been throttled to locally anyway even on a single replica. That
choice is what lets a genuinely new change still get through once the claim expires, rather than
permanently pinning a user to whichever replica happened to win the very first race:

```csharp
// src/backend/Services/ReportingApi/Realtime/TopLinksPushService.cs:161
private static string PushClaimKey(Guid userId) => $"clicks:user:{userId:N}:push-claim";
```

No library dependency beyond the `StackExchange.Redis` client this project already uses for
caching — no Redlock package, no separate lock abstraction. The same `SET ... NX PX` idiom also
backs `RedisLinkDeletionMarkerStore`'s deletion tombstone and `IdempotencyStore`'s replay claim
elsewhere in this codebase, though those two are write-once markers (never contested on purpose),
not a lock guarding a repeated critical section the way this one is.

## Sustained lease: `LinkExpiryWorker`'s leader election

`TopLinksPushService` above claims, works, and lets the TTL run out — the claim's lifetime and the
work's lifetime are the same number. `LinkExpiryWorker` (`ShortenerService`) needs something
different: **exactly one replica should keep being "the leader" indefinitely**, across many
one-minute sweep ticks, until it crashes or is killed. That means the lock has to outlive any
single critical section — so instead of one `SET NX PX` and done, the leader keeps **renewing**
the same key on its own, much faster heartbeat, independently of how often the work it's gating
actually runs.

```mermaid
sequenceDiagram
    participant A as Replica A (leader)
    participant R as Redis
    participant B as Replica B (standby)

    A->>R: SET leader:...:link-expiry A NX PX 15000
    R-->>A: OK - acquired
    loop every RenewInterval (5s)
        A->>R: EVAL RenewLeaseScript (GET == "A"? PEXPIRE : noop)
        R-->>A: 1 - renewed
    end
    B->>R: SET leader:...:link-expiry B NX PX 15000
    R-->>B: refused - key already exists (held by A)
    Note over A: container killed (docker kill)
    Note over R: no more renews arrive - TTL counts down
    R->>R: ~LeaseDuration (15s) later: TTL hits 0, key auto-deleted
    B->>R: SET leader:...:link-expiry B NX PX 15000 (next election tick, within RenewInterval)
    R-->>B: OK - acquired
    Note over B: logs "Acquired link-expiry leadership" - now the only one sweeping
```

Live-verified exactly this way (2 replicas, `docker kill` on the leader's container): the lease
expired ~8s after the kill and the surviving replica's claim succeeded and logged within ~10s of
the kill — both observable directly via `redis-cli GET`/`PTTL leader:shortener-service:link-expiry`
and via the worker's own `"Acquired link-expiry leadership"`/`"Lost link-expiry leadership"` log
lines, or live via control-api's `GET /api/infra/link-expiry-leader` and the architecture-map
panel on the ShortenerService node (polls every 2s).

**Why renewal needs a script, not a client-side `GET` + `PEXPIRE`:**

```csharp
// src/backend/Services/ShortenerService/LinkExpiryWorker.cs:49-55
private const string RenewLeaseScript = """
    if redis.call("GET", KEYS[1]) == ARGV[1] then
        return redis.call("PEXPIRE", KEYS[1], ARGV[2])
    else
        return 0
    end
    """;
```

If the current leader instead did a plain `GET` to check it still owns the key, then a separate
`PEXPIRE` to extend it, there's a window between those two round trips where the lease could
legitimately expire and a different replica could win a fresh claim — and then the *first*
replica's now-stale `PEXPIRE` would land anyway, extending a key that now names someone else,
silently stealing leadership back from a genuinely alive new leader while that new leader has no
idea anything is wrong. Bundling the check and the extend into one Lua script makes Redis execute
both as a single atomic step, server-side, with no window for another client's write to land in
between — the same reason the claim itself uses `SET NX` instead of `EXISTS` then `SET`.

**Fail-safe, not fail-open — the opposite default from `TopLinksPushService` above:**

```csharp
// src/backend/Services/ShortenerService/LinkExpiryWorker.cs:108-117
catch (Exception ex) when (ex is not OperationCanceledException)
{
    // Fail SAFE here, not fail open like TopLinksPushService's push claim: unlike a missed
    // push (one stale dashboard update), an uncoordinated sweep means every replica issuing
    // the same UPDATE on every tick the moment Redis comes back - wasted, not incorrect
    // (the UPDATE's own WHERE guard is idempotent either way), but still the wrong default
    // to reach for just because Redis had a blip.
    _logger.LogWarning(ex, "Leader election check failed for link-expiry - assuming not leader");
    _isLeader = false;
}
```

Same primitive, opposite choice of what to assume when the *coordination mechanism itself* breaks.
`TopLinksPushService` fails open because a missed de-dup there just costs one stale dashboard
update — "proceed anyway" is strictly better than "push nothing." Here, "proceed anyway" (assume
still-leader) would mean every replica racing the same sweep `UPDATE` the instant Redis recovers —
not a correctness bug (the sweep's own `WHERE ... DeletedAt IS NULL` guard is idempotent, same as
the general pattern-doc's point about this project's two singleton jobs needing no fairness), but
still wasted, duplicated work for no benefit over just waiting out a transient blip. Whichever
default is chosen per call site, by what a wrong guess would actually cost — not a library default
applied uniformly.

`LeaseDuration` (15s) is deliberately 3x `RenewInterval` (5s), so one missed renewal (a slow Redis
round trip, a GC pause) doesn't immediately cost leadership — only a leader that's actually gone
for multiple renewal windows loses it. The accepted gap this still leaves: between the old leader
dying and the new leader's claim landing, nothing sweeps at all, bounded by `LeaseDuration` (~15s
worst case) — negligible for a once-a-minute job, same caveat the general pattern-doc already
raises for TTL-based recovery in the abstract.

## Pitfalls

- ⚠️ [Per-process push state plus a cross-replica backplane means every watching replica fires independently](pitfall:signalr-backplane-double-push-no-cluster-claim) (architecture-bug) — fixed

## Relatives

### Nodes

- [ReportingApi](node:reporting-api) — runs `TopLinksPushService`'s one-shot claim
- [ShortenerService](node:shortener-service) — runs `LinkExpiryWorker`'s sustained lease
- [Redis (master)](node:redis-master) — the coordinator both implementations claim against

### Patterns

- [Distributed locking](pattern:distributed-locking) — the general problem and the comparison with the Postgres implementation
