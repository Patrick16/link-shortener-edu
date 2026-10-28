# Per-process push state plus a cross-replica fan-out backplane means every watching replica fires independently

**Category:** architecture-bug **Status:** fixed

Watch for this any time a background loop subscribes to a shared pub/sub channel and decides
locally whether to act, while the thing it does when it acts (a SignalR/WebSocket group send, a
fan-out notification) is delivered through a backplane that reaches *every* server in the cluster,
not just the one that decided to act. Each replica subscribing independently is harmless as long as
only one replica can ever actually deliver the result to a given client. The moment a backplane is
introduced specifically to let *any* replica reach *any* client's connection — which is usually the
whole point of adding one — that assumption breaks: every replica that independently reaches the
same "yes, push now" decision ends up delivering the same result to the same client, once per
replica that decided.

**Concrete instance in this project:** `TopLinksPushService` (per
[ReportingApi](node:reporting-api)'s own node doc) subscribes to Redis's `clicks:user:changed`
channel and tracks which users are locally watched (`TopLinksSubscriptions`) and due for a push
(`TopLinksPushScheduler`) — both deliberately per-process, in-memory state, by their own design.
That was safe before this change because exactly one node process could ever exist (no nginx
fronting it meant no scaling past one replica). This change made >1 replica real (an nginx upstream
plus a `"scalable"` control-plane capability) and wired a SignalR Redis backplane specifically so
`Clients.Group(...)` could reach a user's connection on a *different* replica than the one handling
the push — the exact case of a user's two open tabs/devices landing on different replicas.
Combining those two things exposed the gap: when a user has live connections on two different
replicas, **both** replicas independently see the same Redis pub/sub notification, both
independently find their own local subscription set says this user is watched, both independently
pass their own throttle window, and both independently call `Clients.Group(...)` — and because the
backplane fans that call out to *every* connection in the group regardless of which replica owns
it, each of the user's two connections receives the *same* push **twice** (and the backend did the
read work twice, not once), scaling with however many distinct replicas a user's connections happen
to touch.

⚠️ **Mistake** — `src/backend/Services/ReportingApi/Realtime/TopLinksPushService.cs` (before commit
`335d8a5`):

```csharp
private async Task PushAsync(Guid userId, CancellationToken cancellationToken)
{
    try
    {
        var top = await _reader.GetTopAsync(userId, DateTime.UtcNow, cancellationToken);
        var payload = JsonSerializer.Serialize(top);
        if (_scheduler.IsUnchanged(userId, payload))
        {
            return;
        }
        // ... record payload, re-check still-watched, then:
        await _hub.Clients.Group(TopLinksHub.GroupFor(userId))
            .SendAsync(TopLinksHub.TopLinksMethod, top, cancellationToken);
        // Nothing here stops a DIFFERENT replica, independently handling the same Redis
        // notification, from reaching this exact same point for the same userId at roughly
        // the same time - and the backplane delivers both replicas' sends to every connection
        // in the group.
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Failed to push top links to user {UserId}", userId);
    }
}
```

✅ **Do this instead** (`335d8a5`) — a cluster-wide atomic claim (the same `SET ... NX PX` idiom
[`RedisLinkDeletionMarkerStore`](pitfall:redis-call-cancellation-token-silently-ignored) already
uses elsewhere in this project) before doing any of the real work, so only the replica that wins
the claim proceeds — and fail *open* on a Redis error, so a transient outage degrades to every
replica pushing independently (the pre-backplane behavior) rather than silencing every push:

```csharp
internal async Task PushAsync(Guid userId, CancellationToken cancellationToken)
{
    try
    {
        // Cluster-wide claim, not just this replica's own IsUnchanged/MinPushInterval throttle.
        // SET NX PX, same atomic-claim idiom RedisLinkDeletionMarkerStore already uses elsewhere
        // in this codebase - whichever replica wins proceeds; the TTL (MinPushInterval, the same
        // number this push would have been throttled to locally anyway) is what lets a later,
        // genuinely new change still get through once it expires, rather than permanently
        // locking a user to one replica's pushes.
        if (!await TryClaimPushAsync(userId, cancellationToken))
        {
            return;
        }

        var top = await _reader.GetTopAsync(userId, DateTime.UtcNow, cancellationToken);
        // ... unchanged from here
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Failed to push top links to user {UserId}", userId);
    }
}

// Fail-OPEN, not closed: if Redis itself is unreachable for the claim call, every replica
// proceeding independently (this service's pre-backplane behavior) is still strictly better
// than every replica skipping every push - a transient Redis failure here must degrade to "a
// dashboard might see one duplicate update" territory, never to "no replica pushes anything
// at all" territory.
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
        return true;
    }
}
```

Three regression tests were added (`PushAsync` made `internal` plus a new test-assembly
`InternalsVisibleTo` entry, matching this repo's established pattern for testing consumer-handler-
shaped methods directly): claim succeeds, claim already held by another replica, claim call throws.
Notably, this gap was never actually observed live — the commit that introduced it explicitly
flagged it could not exercise the cross-replica-delivery path on one machine with one source IP; it
was only found by reading the subscription/scheduler/push-service trio together, in light of this
commit's own stated purpose for the backplane.

**Provenance:** found by `feature-review` on the commit that wired the nginx fronting + SignalR
Redis backplane for this node (see `.notes/review-reports/2026-10-28-1316-reporting-api-nginx-
signalr-backplane.md`, F1), fixed in commit `335d8a5`.

## Relatives

### Nodes

- [ReportingApi](node:reporting-api)
- [Redis (master)](node:redis-master)

### Patterns

- [Distributed locking: Redis claims](pattern:distributed-locking-redis) — the general mechanism this fix uses
