# A dead RabbitMQ connection was never detected once it had connected successfully once

**Category:** logic-bug **Status:** fixed

This is the mirror-image bug of [a failed connection attempt being cached
forever](pitfall:rabbitmq-client-connection-never-retried): fixing *that* bug introduced this one.
`IsUsable` decided whether the cached `Task<IConnection>` could be reused by checking only its
`Task.Status` — `Faulted`/`Canceled` meant reconnect, anything else (including `RanToCompletion`)
meant reuse. But a task that ran to completion once stays `RanToCompletion` forever, even after the
`IConnection` it produced is later closed — by a RabbitMQ restart, a Pumba chaos experiment, or an
ordinary network blip. `IsUsable` never checked `IConnection.IsOpen`, so once a service had
connected successfully at least once, every later caller — including the retry loops in
`RabbitMqConsumer`/`RabbitMqPublisher` that exist specifically to recover from a dropped connection
— kept being handed back the same dead connection object. Each retry's `CreateChannelAsync` call
threw immediately, the loop logged, waited, and retried — calling straight back into `IsUsable`,
which still reported the same closed connection "usable." The retry could never succeed even once
the broker came back up; only a full process restart recovered.

🐛 **Bug** — `src/backend/Shared/Infrastructure/RabbitMqClient.cs`, `IsUsable`:

```csharp
private static bool IsUsable(Task<IConnection>? connection) =>
    connection is not null && connection.Status is not (TaskStatus.Faulted or TaskStatus.Canceled);
```

✅ **Fix** — a completed task is only usable if the connection it produced is still actually open;
`GetConnectionAsync` also best-effort disposes the now-dead connection before replacing it:

```csharp
internal static bool IsUsable(Task<IConnection>? connection)
{
    if (connection is null || connection.Status is TaskStatus.Faulted or TaskStatus.Canceled)
    {
        return false;
    }

    // Still connecting - callers will just await it; nothing to check yet.
    if (connection.Status != TaskStatus.RanToCompletion)
    {
        return true;
    }

    // Once a connection attempt succeeds, this task sits at RanToCompletion forever - even after
    // RabbitMQ restarts, a chaos experiment kills the connection, or a network blip drops it.
    // Without checking IsOpen here, every future caller (and the retry loops in
    // RabbitMqConsumer/RabbitMqPublisher) would keep getting handed the same dead IConnection.
    return connection.Result.IsOpen;
}
```

```csharp
// inside GetConnectionAsync, right before reconnecting:
if (existing is { Status: TaskStatus.RanToCompletion })
{
    // IsUsable already found this one dead (IsOpen is false) - dispose it before replacing it so
    // the closed connection's resources aren't held onto forever. A broker-side close may have
    // already torn it down, so a throw here is expected, not exceptional - it must not stop the
    // reconnect below.
    try
    {
        await existing.Result.DisposeAsync().ConfigureAwait(false);
    }
    catch
    {
    }
}
```

The finding was logged against commit `ce8310d` (which introduced this exact `IsUsable`), but the
fix wasn't applied until much later, bundled into commit `3c79e80` — a large, unrelated grab-bag
commit (frontend test additions, a `Sault`→`Salt` migration, a refresh-token cleanup worker,
internal API-key auth). The two snippets above are the isolated hunk, not the full commit diff.

(see review-reports/2026-09-17-2021-ce8310d-rabbitmq-retry-migrations.md, F1 — fixed in `3c79e80`)

## Relatives

### Nodes

- [LinkApi](node:link-api)
- [RedirectApi](node:redirect-api)

### Patterns

- [Async messaging](pattern:async-messaging)
