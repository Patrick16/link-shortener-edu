# A session-scoped advisory lock could be released on the wrong connection

**Category:** connection-pooling **Status:** fixed

A Postgres advisory lock taken with `pg_advisory_lock`/`pg_try_advisory_lock` is session-scoped —
it belongs to one physical connection, and only that same connection's
`pg_advisory_unlock` releases it (or the session ending). An ORM that implicitly opens and closes a
connection around each individual command, by default, gives no guarantee that "acquire" and
"release" run on the same physical connection — "closed" usually just means "returned to the
client-side pool," and the pool is free to hand back a different connector on the next open.

**Concrete instance in this project:** `PartitionMaintenanceWorker.RunOnceAsync` acquired an
advisory lock, did its create/drop DDL work, then released the lock — three separate EF Core calls,
each implicitly opening and closing its own connection by default. If the pool ever handed back a
different physical connector between the acquire and the release, the unlock would hit a session
that never held the lock (a silent no-op from Postgres's point of view), while the session that
*actually* holds it stays connected and keeps holding it — Postgres only releases a session-scoped
advisory lock when that session itself ends. Every future tick's `TryAcquire` would then fail
forever, logged only as "another replica holds the lock" — a permanent, silent deadlock against
itself, with no exception and no obvious symptom beyond partitions simply never being created or
dropped again.

Mistake (`b3d9c7e`, `src/backend/Services/TrafficService/PartitionMaintenanceWorker.cs`) — three
independent EF Core calls, no connection pinned across them:

```csharp
if (!await TryAcquireAdvisoryLockAsync(context, cancellationToken))
{
    _logger.LogDebug("Partition maintenance: another replica holds the lock, skipping this tick");
    return;
}

try
{
    // ... create/drop partitions, each its own EF Core call ...
}
finally
{
    await ReleaseAdvisoryLockAsync(context, cancellationToken);
}
```

Do this instead (`ffdd888`) — `OpenConnectionAsync`/`CloseConnectionAsync` pin one physical
connection open for the whole acquire/work/release section, the pattern EF Core itself documents
for exactly this requirement:

```csharp
await context.Database.OpenConnectionAsync(cancellationToken);
try
{
    if (!await TryAcquireAdvisoryLockAsync(context, cancellationToken))
    {
        _logger.LogDebug("Partition maintenance: another replica holds the lock, skipping this tick");
        return;
    }

    try
    {
        // ... create/drop partitions ...
    }
    finally
    {
        // Released on the exact same physical connection it was acquired on, guaranteed by the
        // explicit OpenConnectionAsync above.
        await ReleaseAdvisoryLockAsync(context, cancellationToken);
    }
}
finally
{
    await context.Database.CloseConnectionAsync();
}
```

**Provenance:** found in a `feature-review` pass (not by any test — the bug needs a real connection-
pool reuse event to manifest, which a single local test run won't reliably trigger), fixed same day.
**Verified live**: rebuilt `traffic-service`, confirmed `pg_locks` showed zero lingering advisory
locks after 3 consecutive container restarts, then re-ran the original create/drop test and got the
same correct result as before the fix. `TrafficService.Tests` 18/18 green.

## Relatives

### Nodes

- [TrafficService](node:traffic-service) — runs the maintenance job this bug was in

### Patterns

- [Data retention](pattern:data-retention-partitioning) — the feature this lock coordinates replicas for
