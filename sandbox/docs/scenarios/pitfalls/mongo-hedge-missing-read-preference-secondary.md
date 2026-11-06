# A hedge pool pinned to a secondary still needs to say so — `directConnection` alone isn't enough

**Category:** architecture-bug **Status:** fixed

A MongoDB driver's default `ReadPreference` is `Primary`, regardless of `directConnection=true`.
Pinning a connection string to one physical node changes *which* node answers, not *what role*
the driver demands of it — so a hedge pool built from direct-connection strings to known
secondaries still has every query rejected outright with `MongoNotPrimaryException` unless
`readPreference=secondary` is appended explicitly. The hedge never even races; it fails before the
primary call has a chance to lose. This went undetected because the only path that ever exercised
a genuine secondary (the hedge) had no test coverage — the non-hedged fallback query that *was*
tested never left the primary.

⚠️ **Mistake** — `src/backend/Shared/Infrastructure/MongoExtensions.cs` (before commit `4e39f0a`):

```csharp
var hedgeNodes = url.Servers
    .Select(server => $"mongodb://{server.Host}:{server.Port}/{url.DatabaseName}?directConnection=true")
    .Where(directConnectionString => !IsCurrentPrimary(directConnectionString))
    .Select(directConnectionString => (IClickMetaQueryService)new MongoClickMetaQueryService(directConnectionString))
    .ToList();
```

✅ **Do this instead** (commit `4e39f0a`):

```csharp
var hedgeNodes = url.Servers
    .Select(server => $"mongodb://{server.Host}:{server.Port}/{url.DatabaseName}?directConnection=true&readPreference=secondary")
    .Where(directConnectionString => !IsCurrentPrimary(directConnectionString))
    .Select(directConnectionString => (IClickMetaQueryService)new MongoClickMetaQueryService(directConnectionString))
    .ToList();
```

Verified live: `GET /reports/{hash}/clicks` went from ~98% failure (500s, `MongoNotPrimaryException`
in the logs) to 100% success under the same 50-VU load.

(see `.notes/feature-docs/2026-11-06-2028-lesson-mode.md` — fixed in `4e39f0a`; no review-report
finding exists for this, it surfaced live running lesson 5.1's own end-to-end sequence for the
first time)

## Relatives

### Nodes

- [Mongo](node:mongo1)
- [ReportingApi](node:reporting-api)

### Patterns

- [Hedged reads](pattern:hedged-reads)
- [High availability & failover](pattern:high-availability-failover)
