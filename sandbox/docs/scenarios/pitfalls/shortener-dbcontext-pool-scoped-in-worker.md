# AddDbContextPool registered as a scoped service inside a Worker Service

**Category:** connection-pooling **Status:** fixed

Watch for `AddDbContextPool<T>` used outside a request-scoped host. `ShortenerService`/
`TrafficService` are .NET generic-host Worker Services — no per-request DI scope exists.
`AddDbContextPool<DatabaseContext>` registers a **scoped**-lifetime service, designed for
request-scoped consumers like ASP.NET Core controllers, where the pool rents a context per request
and returns it at the end of that request. A singleton `IHostedService` cannot directly inject a
scoped service: the DI container either throws
`InvalidOperationException: Cannot consume scoped service ... from singleton ...`, or — with scope
validation off — resolves the pooled context once from the root provider and incorrectly keeps
that single instance alive for the whole process instead of renting/returning one per unit of
work, defeating the pool entirely and leaking stale change-tracker state across every message the
worker ever processes.

⚠️ **Mistake** — the wrong registration for a non-request-scoped host:

```csharp
// ShortenerService/Program.cs
builder.Services.AddDbContextPool<DatabaseContext>(options => { });
```

✅ **Do this instead** — `AddPooledDbContextFactory` + `IDbContextFactory<DatabaseContext>` injected
into the `BackgroundService`, producing one short-lived context per consumed message instead of
holding a DI-scoped instance a singleton was never meant to own:

```csharp
// ShortenerService/Program.cs
builder.Services.AddPooledDbContextFactory<DatabaseContext>(options =>
    options.UseNpgsql(connectionString));
```

```csharp
// LinkCreatedConsumer.cs — one context per batch, not one shared instance
await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
```

(fixed same day in `d88ce9f`)

## Relatives

### Nodes

- [ShortenerService](node:shortener-service)

### Patterns

None yet.
