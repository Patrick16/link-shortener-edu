# AddDbContextPool registered as a scoped service inside a Worker Service

**Category:** logic-bug **Status:** fixed

`ShortenerService`/`TrafficService` are .NET generic-host Worker Services — no per-request DI
scope exists. `AddDbContextPool<DatabaseContext>` registers a **scoped**-lifetime service, designed
for request-scoped consumers like ASP.NET Core controllers. A singleton `IHostedService` cannot
directly inject a scoped service: the DI container either throws
`InvalidOperationException: Cannot consume scoped service ... from singleton ...`, or — with scope
validation off — resolves the pooled context once from the root provider and incorrectly keeps that
single instance alive for the whole process, instead of renting/returning one per unit of work.

🐛 **Bug** — the wrong registration for a non-request-scoped host:

```csharp
// ShortenerService/Program.cs
builder.Services.AddDbContextPool<DatabaseContext>(options => { });
```

✅ **Fix** — `AddPooledDbContextFactory` + `IDbContextFactory<DatabaseContext>` injected into the
`BackgroundService`, producing one short-lived context per consumed message instead of holding a
DI-scoped instance a singleton was never meant to own:

```csharp
// ShortenerService/Program.cs
builder.Services.AddPooledDbContextFactory<DatabaseContext>(options =>
    options.UseNpgsql(connectionString));
```

```csharp
// LinkCreatedConsumer.cs — one context per batch, not one shared instance
await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
```

(see review-reports/2026-09-17-1631-b09788d-pooled-dbcontext-scalar.md, F1 — fixed same day in
`d88ce9f`)

## Relatives

### Nodes

- [ShortenerService](node:shortener-service)

### Patterns

None yet.
