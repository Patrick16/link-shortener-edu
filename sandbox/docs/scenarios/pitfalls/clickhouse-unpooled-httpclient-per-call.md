# A ClickHouse client registered as a singleton still opened a fresh HttpClient on every call

**Category:** connection-pooling **Status:** fixed

Watch for the gap between "this type is registered as a DI singleton" and "the expensive resource
it wraps is actually shared." `ClickHouseClickFactStore`/`ClickHouseClickFactQueryService` (the
write/read sides of the ClickHouse read model — see [CQRS fan-out](pattern:cqrs-fanout)) are both
registered as singletons, but every method (`InsertManyAsync`, `PingAsync`, `GetSummaryAsync`)
originally constructed a brand-new `ClickHouseClient`/`ClickHouseConnection` straight from the
connection string, on every single call — which, under the hood, built its own one-off `HttpClient`
each time rather than reusing anything the singleton held. The singleton registration gave no
actual pooling: under any real request rate, every query or insert opened and tore down a fresh
HTTP connection to ClickHouse's port 8123, churning sockets/TCP handshakes on a path that looked
pooled but wasn't.

**No real before/after diff exists for this one** — it was caught and fixed in the same
uncommitted working-tree session the feature was built in (review happened before the first
commit), so there's no prior commit showing the buggy state. The "Mistake" snippet below is
reconstructed from the finding's own description, not pulled from `git show`.

⚠️ **Mistake** — each call built its own `HttpClient` by constructing the driver type directly
from the connection string, with no `IHttpClientFactory` involved at all:

```csharp
// reconstructed shape, not a real diff — see note above
public sealed class ClickHouseClickFactQueryService(string connectionString) : IClickFactQueryService
{
    public async Task<ClickSummary> GetSummaryAsync(string hash, Guid callerUserId, CancellationToken ct = default)
    {
        await using var connection = new ClickHouseConnection(connectionString); // new HttpClient every call
        // ...
    }
}
```

✅ **Do this instead** — `src/backend/Shared/Infrastructure/ClickHouseExtensions.cs` registers one
named `HttpClient` via `IHttpClientFactory`, and both store/query classes take the factory + that
name instead of a bare connection string:

```csharp
// src/backend/Shared/Infrastructure/ClickHouseExtensions.cs:24-31
public static IHostApplicationBuilder AddClickHouseWriter(this IHostApplicationBuilder builder)
{
    builder.Services.AddHttpClient(HttpClientName);
    var connectionString = builder.Configuration.GetConnectionString(Constants.ClickHouseConnectionString);
    builder.Services.AddSingleton<IClickFactStore>(sp => new ClickHouseClickFactStore(
        connectionString!, sp.GetRequiredService<IHttpClientFactory>(), HttpClientName));
    return builder;
}
```

```csharp
// src/backend/Shared/Infrastructure/ClickHouseClickFactStore.cs:47-51
// A fresh ClickHouseClient per call is cheap - it holds no socket of its own. What's expensive
// (and was the actual problem found during review) is handing it a one-off HttpClient instead
// of one borrowed from IHttpClientFactory: the factory keeps the real connection pool alive and
// reused across calls regardless of how many short-lived ClickHouseClient wrappers sit on top.
private ClickHouseClient NewClient() => new(_connectionString, _httpClientFactory, _httpClientName);
```

A fresh `ClickHouseClient`/`ClickHouseConnection` wrapper per call is still necessary (ADO.NET
connections aren't safe to share across concurrent calls) — the fix isn't "stop creating the
wrapper," it's "stop letting the wrapper build its own unpooled transport underneath."

(see review-reports/2026-10-05-2356-cqrs-reporting-clickhouse.md, F5 — fixed before first commit,
no diffable hash; see note above)

## Relatives

### Nodes

- [ReportingService](node:reporting-service) — write side (`InsertManyAsync`)
- [ReportingApi](node:reporting-api) — read side (`GetSummaryAsync`)

### Patterns

- [CQRS fan-out](pattern:cqrs-fanout)
