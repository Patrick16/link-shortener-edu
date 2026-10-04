# Scaling link-api/redirect-api made the SQLite fallback queue republish messages twice

**Category:** race-condition **Status:** fixed

`link-api` and `redirect-api` are designed to run as multiple replicas
(`docker compose up --scale link-api=N`). Before their SQLite fallback file (used when RabbitMQ is
briefly unreachable — see [async messaging](pattern:async-messaging)) was routed into a named
Docker volume, each replica's fallback database lived on its own ephemeral container filesystem, so
scaling was safe by accident: every replica had a private copy. Once the fallback path was pointed
at a shared named volume (so the file survives container recreation and can be inspected via a
viewer), every replica of a service started sharing one physical SQLite file and one
`FailedMessages` table — but `GetPendingAsync` was still a plain `SELECT ... ORDER BY CreatedAt`
with no row-claiming, and each replica's `RabbitMqRetryWorker` polls independently on its own 30s
timer. Two replicas' ticks could both `SELECT` the same pending row before either deleted it, both
successfully republish it, and the event would be processed twice downstream —
`RabbitMqConsumer` has no message-id dedup on the consuming side, only a tracing tag.

🐛 **Bug** — `src/backend/Shared/Infrastructure/SqliteMessageFallbackStore.cs`, `GetPendingAsync`:
nothing marks a row as "being worked on", so a second concurrent caller sees it too.

```csharp
await using var command = connection.CreateCommand();
command.CommandText = $"SELECT MessageId, Topic, Payload, CreatedAt FROM {TableName} ORDER BY CreatedAt LIMIT {MaxPendingPerFetch};";

var results = new List<FallbackMessage>();
await using var reader = await command.ExecuteReaderAsync(cancellationToken);
while (await reader.ReadAsync(cancellationToken))
{
    results.Add(new FallbackMessage(/* ... */));
}

return results;
```

✅ **Fix** — replace the plain `SELECT` with a single atomic `UPDATE ... RETURNING` that claims
the rows (stamps `ClaimedAt`) as part of the same statement that selects them. SQLite serializes
writers, so a second replica's concurrent call can only ever see rows the first replica's call
didn't already claim by the time its own `UPDATE` runs. A claim that's never resolved (its replica
crashed mid-tick) self-heals after a 2-minute staleness threshold, matched by the same `WHERE`
clause — no explicit "unclaim" step needed:

```csharp
private static readonly TimeSpan StaleClaimThreshold = TimeSpan.FromMinutes(2);

// ...

await using var command = connection.CreateCommand();
command.CommandText = $"""
    UPDATE {TableName}
    SET ClaimedAt = $now
    WHERE MessageId IN (
        SELECT MessageId FROM {TableName}
        WHERE ClaimedAt IS NULL OR ClaimedAt <= $staleThreshold
        ORDER BY CreatedAt
        LIMIT {MaxPendingPerFetch}
    )
    RETURNING MessageId, Topic, Payload, CreatedAt;
    """;
var now = DateTime.UtcNow;
command.Parameters.AddWithValue("$now", now.ToString("O"));
command.Parameters.AddWithValue("$staleThreshold", (now - StaleClaimThreshold).ToString("O"));

var results = new List<FallbackMessage>();
await using var reader = await command.ExecuteReaderAsync(cancellationToken);
while (await reader.ReadAsync(cancellationToken))
{
    results.Add(new FallbackMessage(/* ... */));
}

// RETURNING reflects the order rows were physically updated in, not the subquery's own ORDER BY
// (which only picked which rows to include, via LIMIT) - re-sorting here is what actually
// delivers on GetPendingAsync's documented oldest-first contract.
results.Sort((a, b) => a.CreatedAt.CompareTo(b.CreatedAt));
return results;
```

`ClaimedAt` needed a migration path too: SQLite has no `ADD COLUMN IF NOT EXISTS`, so a pre-existing
fallback file (from before this fix) needs a `pragma_table_info` check plus a conditional
`ALTER TABLE` before the column can be relied on. `RabbitMqRetryWorker` itself needed no changes —
it still just calls `GetPendingAsync` then `DeleteAsync` on success.

The finding was logged against commit `cc25af8` (which routed the fallback file into a shared
volume), but the fix wasn't applied until later, bundled into commit `3c79e80` — a large, unrelated
grab-bag commit (frontend test additions, a `Sault`→`Salt` migration, a refresh-token cleanup
worker, internal API-key auth). The snippets above are the isolated hunk, not the full commit diff.

(fixed in `3c79e80`)

## Relatives

### Nodes

- [LinkApi](node:link-api)
- [RedirectApi](node:redirect-api)

### Patterns

- [Async messaging](pattern:async-messaging) — the SQLite fallback queue this bug lives in
