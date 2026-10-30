# A last-resort fallback write needs its own protection — it has no fallback of its own

**Category:** resilience-gap **Status:** fixed

Watch for this any time a system has a primary path and a fallback path for the same operation,
and the fallback is treated as inherently safe just because it's "only" a local write (SQLite, a
local file, an in-memory queue). The fallback is still real code that can still fail — and because
it's the thing everything else already failed over to, there is nowhere further to fail over *to*.
Two separate gaps tend to hide behind that assumption: the fallback's own failure modes under
realistic conditions (contention, disk pressure) are never tuned, and the call site invoking the
fallback doesn't wrap it in anything, so an exception from the fallback is indistinguishable from
any other "something unexpected happened" log line further up the stack — the one case where the
event is actually gone for good looks exactly like every other case where it probably isn't.

**Concrete instance in this project:** `RabbitMqPublisher.PublishAsync` falls back to
`SqliteMessageFallbackStore.SaveAsync` when a live RabbitMQ publish fails or is cancelled (drained
back to the broker later by `RabbitMqRetryWorker`). Two gaps, both found in the same review pass:

1. `SqliteMessageFallbackStore`'s connection string set no busy-timeout. `Microsoft.Data.Sqlite`'s
   default behavior under write contention is to throw `SQLITE_BUSY` **immediately**, not to wait
   — and the class's own pre-existing comment on `StaleClaimThreshold` already documents that
   multiple service replicas share one SQLite file. Two replicas' fallback publishes landing within
   milliseconds of each other — not an outage, just ordinary concurrent load — could already lose an
   event this way.
2. The fallback save itself was called with no protection of its own: if it threw, the exception
   propagated up to `LocalPublishQueueWorker`'s existing generic catch, which already had a comment
   acknowledging "something unexpected escaped" but logged this case indistinguishably from any
   other failure — with the event now gone for good, and nothing in the logs flagging that
   specifically.

⚠️ **Mistake** — `src/backend/Shared/Infrastructure/SqliteMessageFallbackStore.cs` (constructor,
before `5637b49`) and `RabbitMqPublisher.cs` (the fallback-save call site, same commit):

```csharp
// SqliteMessageFallbackStore.cs - no busy-timeout set at all; Microsoft.Data.Sqlite defaults to
// failing SQLITE_BUSY immediately under write contention instead of waiting.
public SqliteMessageFallbackStore(string connectionString)
{
    _connectionString = connectionString;
    EnsureTableCreated();
}
```

```csharp
// RabbitMqPublisher.cs - the last line of defense, called with no protection of its own; a
// failure here propagates up to a generic catch indistinguishable from any other exception.
await _fallbackStore.SaveAsync(
    new FallbackMessage(messageId, topic, payload, DateTime.UtcNow),
    CancellationToken.None);
```

✅ **Do this instead** (`5637b49`) — give the fallback store a real busy-timeout so it waits through
ordinary contention instead of failing instantly, and wrap the call site so this specific failure
mode logs as exactly what it is before being rethrown:

```csharp
// SqliteMessageFallbackStore.cs
// SQLite's own busy handler blocks-and-retries internally for up to this many seconds before
// giving up - 5s comfortably covers a single-row SaveAsync/DeleteAsync without risking a real
// deadlock (SQLite's busy handler can't deadlock with itself - it's one writer waiting for
// another to finish its own short transaction).
private const int BusyTimeoutSeconds = 5;

public SqliteMessageFallbackStore(string connectionString)
{
    var builder = new SqliteConnectionStringBuilder(connectionString) { DefaultTimeout = BusyTimeoutSeconds };
    _connectionString = builder.ConnectionString;
    EnsureTableCreated();
}
```

```csharp
// RabbitMqPublisher.cs
// This is the actual last line of defense - if it throws, the event is gone for good, not just
// "still in the fallback queue". Logged here, at the exact point of loss with the message's own
// id/topic, rather than left to whichever generic catch happens to be further up the call stack -
// then rethrown so that catch still sees it and its own accounting stays accurate.
try
{
    await _fallbackStore.SaveAsync(
        new FallbackMessage(messageId, topic, payload, DateTime.UtcNow),
        CancellationToken.None);
}
catch (Exception ex)
{
    _logger.LogError(ex, "Message {MessageId} on topic {Topic} is PERMANENTLY LOST - RabbitMQ publish failed and the SQLite fallback write also failed", messageId, topic);
    throw;
}
```

New tests verify both halves do what they claim, not just "doesn't throw": a real held-SQLite-lock
test proving `SaveAsync` actually *waits* and then succeeds once the lock releases, plus a publisher
test exercising the new log-and-rethrow path.

(not sourced from a `.notes/review-reports/` finding — found and fixed same-day as part of the
external code review batch; see `.notes/feature-docs/2026-10-30-1524-external-review-10-bugs.md`,
bug 3, and `.notes/PLAN.md`'s "Feature: External Review Findings" section — fixed in `5637b49`)

## Relatives

### Nodes

- [LinkApi](node:link-api) — publishes through this fallback path on a broker outage
- [RedirectApi](node:redirect-api) — same
- [RabbitMQ](node:rabbitmq)

### Patterns

- [Async messaging](pattern:async-messaging) — the SQLite fallback queue this fix hardens
