# A `docker compose up --scale` call without `--no-deps` silently restarts whatever it depends on

**Category:** infrastructure-bug **Status:** fixed

`docker compose up`'s default behavior is to also bring up a target service's whole `depends_on`
chain to the condition it declares — that's exactly what you want for a normal stack start, and
exactly what you don't want for a narrow, surgical "just change this one thing" call issued by a
control plane that may have *deliberately* stopped one of those dependencies moments earlier. If a
service you're scaling/recreating declares `depends_on: ... condition: service_healthy` on
something else, every `docker compose up` call that targets it needs `--no-deps` explicitly, every
time — there's no way to opt out per-dependency, and forgetting it on even one call in a class of
otherwise-identical calls reintroduces the whole problem that class exists to avoid. Watch for this
any time a control plane wraps `docker compose` directly instead of the Docker Engine API: the
convenience of one compose command standing in for "start/scale this service" comes with this
default attached.

**Concrete failure scenario in this project:** `pgcat` depends on `postgres`/`postgres-replica1`/
`postgres-replica2` all reaching `service_healthy`. The HAProxy toggle scales `pgcat` to 1 replica
when HAProxy goes off and back to 3 when it comes back on, via its own narrow `docker compose up
--scale pgcat=N` call — unrelated, on paper, to anything about Postgres's replica containers.
Toggle the separate "Read replicas (Postgres)" control OFF first (stopping `postgres-replica1`/
`postgres-replica2`, deliberately, keeping their volumes), then toggle HAProxy OFF: the pgcat-scale
call, lacking `--no-deps`, also brought `postgres-replica1`/`postgres-replica2` back up as a side
effect of scaling `pgcat` down — silently undoing the other toggle's own state, with no error and no
log line pointing at the actual cause.

⚠️ **Mistake** — `sandbox/infra/control-api/Services/InfraToggleService.cs`
(`SyncPgcatReplicaCountAsync`, as shipped in `b17ec90`):

```csharp
var (exitCode, output) = await runtime.RunComposeAsync(
    ["up", "-d", "--scale", $"pgcat={replicas}", "--no-recreate", "pgcat"], env, ct);
```

Every *other* `RunComposeAsync` call in this same class already carried `--no-deps` for the
identical reason (a prior, separately-learned lesson — see this file's own neighboring comment on
`SetPgcatEnabledAsync`) — this was the one command that didn't.

✅ **Do this instead** — treat `--no-deps` as load-bearing on every targeted compose call in a
system where containers get deliberately stopped out of band, not just a flag to add once and
forget:

```csharp
var (exitCode, output) = await runtime.RunComposeAsync(
    ["up", "-d", "--scale", $"pgcat={replicas}", "--no-recreate", "--no-deps", "pgcat"], env, ct);
```

**A related, second lesson fixed in the same commit:** the mirror-image gap. `SetHaproxyEnabledAsync`
kept pgcat's replica count in lockstep with HAProxy's own state, but `SetPgcatEnabledAsync(false)` —
which also force-stops HAProxy, as a one-directional cascade — never called the same
`SyncPgcatReplicaCountAsync` helper at all:

```csharp
// before - haproxy stops, but pgcat is never told to collapse back to 1 replica
if (!enabled)
{
    await SyncHaproxyContainerAsync(false, ct);
}
```
```csharp
// after - the same invariant ("always exactly 1 or 3, in lockstep with haproxy") is enforced
// from this code path too, not just from SetHaproxyEnabledAsync's own
if (!enabled)
{
    await SyncHaproxyContainerAsync(false, ct);
    await SyncPgcatReplicaCountAsync(1, ct);
}
```

A one-directional cascade (`A` forces `B` off) is easy to get half right: it's correct about the
resource it directly names (`haproxy`) but silently forgets a *second* piece of state (`pgcat`'s
replica count) that a different code path already keeps in sync for the common entry point. Both
gaps here were masked by the only shipped UI path (the Topology panel's Save always reapplies both
toggles' full desired state together) — they were only reachable by a caller using either
control-api endpoint independently, e.g. a script or `curl`.

(see review-reports/2026-10-16-0240-haproxy-pgcat-replica-scaling.md, F1/F2 — fixed in `ae134b6`)

## Relatives

### Nodes

- [PgCat](node:pgcat) — the service being scaled, and the depends_on chain that got pulled in by accident
- [HAProxy](node:haproxy) — the toggle this scaling coupling exists for

### Patterns

- [Pooler scaling](pattern:pooler-scaling) — the `×N` badge this coupling keeps honest
