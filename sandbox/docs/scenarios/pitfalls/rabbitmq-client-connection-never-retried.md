# A failed RabbitMQ connection attempt was cached forever

**Category:** logic-bug **Status:** fixed

`RabbitMqClient`'s constructor started `factory.CreateConnectionAsync()` once and stored the
resulting `Task<IConnection>` in a `readonly` field; every later channel request awaited that same
task. If the very first connection attempt failed — a common ordering issue when RabbitMQ's
container is still booting while LinkApi/RedirectApi start — the task was permanently faulted:
every later await re-threw the *original* exception, with no retry, until the whole process was
restarted. Every publish fell through to the SQLite fallback queue permanently, even once RabbitMQ
became healthy seconds later.

🐛 **Bug** — connection attempted once, eagerly, in the constructor:

```csharp
public RabbitMqClient(IConfiguration configuration)
{
    var factory = new ConnectionFactory { /* ... */ };
    _connection = factory.CreateConnectionAsync(); // awaited once, cached forever - including failures
}
```

✅ **Fix** — lazy connection on first use, with a faulted attempt discarded so the next caller gets
a fresh try instead of the same cached exception:

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

(see review-reports/2026-09-17-1627-22bcacf-linkapi-rabbitmq-publisher.md, F1 — fixed in `ce8310d`)

## Relatives

### Nodes

- [LinkApi](node:link-api)
- [RedirectApi](node:redirect-api)
- [RabbitMQ](node:rabbitmq)

### Patterns

- [Async messaging](pattern:async-messaging)
