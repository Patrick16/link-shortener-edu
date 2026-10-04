# A dead RabbitMQ connection was never detected once it had connected successfully once

**Category:** resilience-gap **Status:** fixed

This is the mirror-image mistake of [caching a failed connection attempt
forever](pitfall:rabbitmq-client-connection-never-retried): fixing *that* one introduces this one if
you're not careful. Watch for a "reuse this cached connection?" check that only looks at the task
wrapping the connection, not the connection object itself: `IsUsable` deciding whether the cached
`Task<IConnection>` can be reused by checking only its `Task.Status` — `Faulted`/`Canceled` means
reconnect, anything else (including `RanToCompletion`) means reuse — misses that a task which ran
to completion once stays `RanToCompletion` forever, even after the `IConnection` it produced is
later closed by a RabbitMQ restart, a Pumba chaos experiment, or an ordinary network blip. Without
also checking `IConnection.IsOpen`, once a service has connected successfully at least once, every
later caller — including the retry loops in `RabbitMqConsumer`/`RabbitMqPublisher` that exist
specifically to recover from a dropped connection — keeps being handed back the same dead
connection object. Each retry's `CreateChannelAsync` call throws immediately, the loop logs, waits,
and retries — calling straight back into `IsUsable`, which still reports the same closed connection
"usable." The retry can never succeed even once the broker comes back up; only a full process
restart recovers.

⚠️ **Mistake** — `src/backend/Shared/Infrastructure/RabbitMqClient.cs`, `IsUsable`:

```csharp
private static bool IsUsable(Task<IConnection>? connection) =>
    connection is not null && connection.Status is not (TaskStatus.Faulted or TaskStatus.Canceled);
```

✅ **Do this instead** — a completed task is only usable if the connection it produced is still
actually open; `GetConnectionAsync` also best-effort disposes the now-dead connection before
replacing it:

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

(fixed in `3c79e80`)

## Relatives

### Nodes

- [LinkApi](node:link-api)
- [RedirectApi](node:redirect-api)

### Patterns

- [Async messaging](pattern:async-messaging)
