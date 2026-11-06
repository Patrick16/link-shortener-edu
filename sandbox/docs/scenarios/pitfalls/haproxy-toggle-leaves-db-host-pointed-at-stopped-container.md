# A derived env value only accounted for one of two toggles that affect it

**Category:** architecture-bug **Status:** fixed

When two standing toggles layer in front of the same resource (here: pgcat routes to Postgres,
HAProxy routes to pgcat), a value derived from one of them has to be derived from *both*, or
toggling the outer one leaves the derived value silently pointed at whatever the inner toggle last
saw. `DB_HOST` was computed purely from "is pgcat enabled", so turning the HAProxy toggle off
(stopping that container) left `DB_HOST` still saying `haproxy` — every DB-touching request then
hung until its own connection attempt timed out, regardless of whether the pgcat replica
underneath was perfectly healthy, which is the opposite of the single-point-of-failure story that
toggle exists to demonstrate.

⚠️ **Mistake** — `sandbox/infra/control-api/Services/InfraToggleService.cs` (before commit `160c842`):

```csharp
internal Dictionary<string, string> CurrentStandingEnv() => new()
{
    ["DB_HOST"] = _pgcatEnabled ? "haproxy" : "postgres",
    // ...
};

public Task<InfraStatus> SetHaproxyEnabledAsync(bool enabled, CancellationToken ct) =>
    runtime.RunExclusiveToggleAsync(async ct =>
    {
        await SyncHaproxyContainerAsync(enabled, ct);
        await SyncPgcatReplicaCountAsync(enabled ? 3 : 1, ct);
        return await GetInfraStatusAsync(ct);
    }, ct);
```

`SetHaproxyEnabledAsync` stopped the `haproxy` container but never touched the env var every
DB-touching service's connection string was built from — `_pgcatEnabled` alone decided `DB_HOST`,
and it stayed `true` the whole time.

✅ **Do this instead** (commit `160c842`) — derive the host from both flags via one shared
resolver, and recreate the dependent services with the recomputed env so the connection string
actually changes:

```csharp
private static string ResolveDbHost(bool pgcatEnabled, bool haproxyEnabled) =>
    !pgcatEnabled ? "postgres" : haproxyEnabled ? "haproxy" : "pgcat";

internal Dictionary<string, string> CurrentStandingEnv() => new()
{
    ["DB_HOST"] = ResolveDbHost(_pgcatEnabled, _haproxyEnabled),
    // ...
};

public Task<InfraStatus> SetHaproxyEnabledAsync(bool enabled, CancellationToken ct) =>
    runtime.RunExclusiveToggleAsync(async ct =>
    {
        var env = CurrentStandingEnv();
        env["DB_HOST"] = ResolveDbHost(_pgcatEnabled, enabled);
        var scaleArgs = await runtime.BuildPreserveScaleArgsAsync(DbTouchingServices, ct);

        // Enabling: bring haproxy up first, then point DbTouchingServices at it. Disabling: the
        // reverse order - point them at "pgcat" before haproxy's container actually stops.
        if (enabled)
        {
            await SyncHaproxyContainerAsync(true, ct);
            await SyncPgcatReplicaCountAsync(3, ct);
        }

        await runtime.RunComposeAsync(
            ["up", "-d", "--force-recreate", "--no-deps", .. scaleArgs, .. DbTouchingServices], env, ct);

        if (!enabled)
        {
            await SyncHaproxyContainerAsync(false, ct);
            await SyncPgcatReplicaCountAsync(1, ct);
        }

        _haproxyEnabled = enabled;
        return await GetInfraStatusAsync(ct);
    }, ct);
```

`_pgcatEnabled` had the identical bug already (enabling pgcat always said `haproxy`, even if that
container happened to be stopped) — fixed by the same `ResolveDbHost` call, not a second one-off
patch. The host now resolves to `postgres` with pgcat off, `haproxy` with both on, and the bare
`pgcat` service name (Compose round-robins it across whatever replicas remain, just `pgcat-1` once
HAProxy is off) with pgcat on but HAProxy off.

Verified live: with the fix, `POST /register` succeeds instantly with HAProxy off and pgcat
healthy, and only fails once pgcat itself is actually degraded.

(see `.notes/feature-docs/2026-11-06-2028-lesson-mode.md` — fixed in `160c842`; no review-report
finding exists for this, it surfaced live while testing lesson 4.7 "Pooler Loses an Instance")

## Relatives

### Nodes

- [HAProxy](node:haproxy)
- [PgCat](node:pgcat)

### Patterns

- [Pooler scaling](pattern:pooler-scaling)
