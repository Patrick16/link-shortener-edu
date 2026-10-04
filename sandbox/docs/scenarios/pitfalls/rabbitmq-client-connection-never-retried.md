# A failed RabbitMQ connection attempt was cached forever

**Category:** resilience-gap **Status:** fixed

Watch for a client that attempts a connection to an external broker exactly once, eagerly, at
startup, and caches whatever that attempt produces — success or failure — forever. If
`RabbitMqClient`'s constructor starts `factory.CreateConnectionAsync()` once and stores the
resulting `Task<IConnection>` in a `readonly` field, every later channel request awaits that same
task. If the very first connection attempt fails — a common ordering issue when RabbitMQ's
container is still booting while LinkApi/RedirectApi start — the task is permanently faulted:
every later await re-throws the *original* exception, with no retry, until the whole process is
restarted. Every publish falls through to the SQLite fallback queue permanently, even once
RabbitMQ becomes healthy seconds later — an infra-startup race turned into a standing outage by the
client's own caching.

⚠️ **Mistake** — connection attempted once, eagerly, in the constructor:

```csharp
public RabbitMqClient(IConfiguration configuration)
{
    var factory = new ConnectionFactory { /* ... */ };
    _connection = factory.CreateConnectionAsync(); // awaited once, cached forever - including failures
}
```

✅ **Do this instead** — lazy connection on first use, with a faulted attempt discarded so the next
caller gets a fresh try instead of the same cached exception:

```csharp
private async Task<IConnection> GetConnectionAsync(CancellationToken ct)
{
    if (IsUsable(_connection)) return await _connection;

    await _connectionLock.WaitAsync(ct);
    try
    {
        if (!IsUsable(_connection))
        {
            // A failed attempt is not cached: the next caller gets a fresh connection attempt
            // instead of forever re-throwing the first failure.
            _connection = _factory.CreateConnectionAsync(ct);
        }
        return await _connection;
    }
    finally { _connectionLock.Release(); }
}
```

(fixed in `ce8310d`)

## Relatives

### Nodes

- [LinkApi](node:link-api)
- [RedirectApi](node:redirect-api)
- [RabbitMQ](node:rabbitmq)

### Patterns

- [Async messaging](pattern:async-messaging)
