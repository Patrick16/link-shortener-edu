# Per-user push state that never got evicted

**Category:** memory-leak **Status:** fixed

State kept per user ("when did I last push, what did I send") needs eviction tied to the lifetime of
the thing it tracks - here, the user's open dashboard connection - not to an event that may never
recur. If entries are removed only as a side effect of the work loop, every key that stops generating
work stays in memory for the life of the process.

Watch for this any time a long-lived background loop keeps a dictionary keyed by user or connection.
The leak scales with how many distinct keys ever show up, not with how many are active at once.

**Concrete instance in this project:** the live top-links push service (ReportingApi) tracked each
user's last-sent payload. Its Redis channel handler marked every link owner as dirty, not only users
with a dashboard open. Entries were removed only inside the push loop, and only for users that were
both dirty and unwatched. Most owners never open a dashboard, so every owner whose link was ever
clicked left state behind. The leak scaled with distinct owners, not with open connections.

Mistake - **reconstructed**: this intermediate shape was never committed, so it is rebuilt from the
description of the bug rather than copied from history:

```csharp
// TopLinksPushService (original, reconstructed)
private readonly ConcurrentDictionary<Guid, string> _lastSent = new();

// channel handler: marks EVERY owner dirty
if (Guid.TryParseExact(message.ToString(), "N", out var userId))
    _scheduler.MarkDirty(userId);

foreach (var userId in _scheduler.TakeDue(DateTime.UtcNow, MinPushInterval))
{
    if (!_subscriptions.IsWatched(userId))
    {
        _lastSent.TryRemove(userId, out _);   // the only removal
        continue;
    }
    // ...
}
```

The version that was actually committed in bd47119 is a partial form of the same leak, and it is
diffable: per-user state had already moved into `TopLinksPushScheduler`, and the hub already called
`Forget()`. But the channel handler still marked everyone dirty, and the unwatched branch only did
`continue`. `TakeDue` wrote `_lastPushedUtc[userId]` for every dirty user, so each unwatched owner still
left an entry behind.

Do this instead (the fix in a2b405e, `git diff bd47119 a2b405e -- src/backend/Services/ReportingApi/Realtime/`):

```csharp
// channel handler: only watched users are tracked at all
if (Guid.TryParseExact(message.ToString(), "N", out var userId) && _subscriptions.IsWatched(userId))
    _scheduler.MarkDirty(userId);

// loop: an unwatched user is dropped, not just skipped
if (!_subscriptions.IsWatched(userId))
{
    _scheduler.Forget(userId);
    continue;
}

// PushAsync: record first, then re-check. A disconnect that lands during the query would otherwise
// run Forget() before RecordPayload re-adds the payload.
_scheduler.RecordPayload(userId, payload);
if (!_subscriptions.IsWatched(userId))
{
    _scheduler.Forget(userId);
    return;
}
```

All per-user state now lives in `TopLinksPushScheduler`, and `Forget()` (`TopLinksPushScheduler.cs:60`)
clears the dirty set, the last-push time and the last payload in one call. `TopLinksHub.OnDisconnectedAsync`
calls it when the last connection for a user closes. A side benefit: a user who reconnects gets an immediate
first push, because no throttle window is left over.

**Provenance:** the "before" in the main example is reconstructed. The bd47119 to a2b405e diff is real.
Verified by reading the code and by `TopLinksPushSchedulerTests`. Memory growth was not measured under load.

## Relatives

### Nodes

- [Redis](node:redis-master) — its pub/sub channel drives the push loop that marked every owner dirty

### Patterns

- [Caching](pattern:caching) — the click day buckets and the channel the push service listens on live on the same Redis
