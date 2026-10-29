# Assuming the first host in a replica-set connection string is always the primary

**Category:** architecture-bug **Status:** fixed

Watch for any code that picks a MongoDB replica-set node to treat specially (exclude from a hedge
pool, route admin commands to, whatever) based on its **position** in the connection string or
`MONGO_HOSTS`-style config, rather than asking the replica set who's actually primary **right now**.
A replica set's primary is whichever member currently won election — nothing pins it to the first
name in a host list, and a real failover can and will reassign it to any member. Code that assumes
otherwise is silently wrong exactly when it matters most (right after a real failover), with no
error or warning to flag the mistake.

⚠️ **Mistake** — `src/backend/Shared/Infrastructure/MongoExtensions.cs` (commit `da445ba`):

```csharp
// "mongo1 is the primary, hedge against the other two"
var hedgeNodes = url.Servers.Skip(1).Select(server => /* build a direct-connected query service */);
```

This project's own Mongo replica set does *not* have a fixed primary — `mongo1.md` already
documents a real run where a fresh stack came up with `mongo3` as primary, and
`.notes/PLAN.md`'s own 2026-10-06 incident records `mongo2` ending up primary after a failover. With
the code above, hedging would have silently queried the real primary directly (extra, unnecessary
load on the node actually serving writes) while leaving the real secondary out of the hedge pool
entirely — no exception, no log line, just a quietly wrong topology assumption.

✅ **Do this instead** (commit `8a654f1`) — ask each candidate node, live, which one currently
holds the primary role, and exclude whichever one answers true:

```csharp
// IsCurrentPrimary: a synchronous `hello` command (isWritablePrimary - MongoDB 5.0+'s replacement
// for the legacy `ismaster` field) against a direct-connected candidate, run once at startup.
// Whichever node answers true is excluded from the hedge pool - never assumed by position.
var hedgeNodes = url.Servers
    .Where(server => !IsCurrentPrimary(BuildDirectConnectionString(server)))
    .Select(server => /* build a direct-connected query service */);
```

Live-verified against this exact failure mode: at verification time, `rs.status()` on the running
dev stack showed `mongo1: SECONDARY, mongo2: PRIMARY, mongo3: SECONDARY` — the scenario the bug was
about, happening for real — and the fixed code correctly excluded `mongo2`, not `mongo1`.

The check runs once at process startup, not per request — if the replica set's primary changes
*after* the hedging service has already started, the hedge pool keeps using the primary it detected
at boot until the process restarts. Worth knowing, not itself the bug this doc covers.

(see `.notes/review-reports/2026-10-29-2230-hedged-reads-feature.md` — fixed in `8a654f1`)

## Relatives

### Nodes

- [Mongo](node:mongo1)
- [ReportingApi](node:reporting-api)

### Patterns

- [Hedged reads](pattern:hedged-reads)
- [High availability & failover](pattern:high-availability-failover)
