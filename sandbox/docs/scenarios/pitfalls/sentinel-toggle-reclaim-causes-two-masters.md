# Reclaiming a node back to "master" is only safe if nothing else already holds that role

**Category:** resilience-gap
**Status:** fixed

A recovery step that's a documented no-op *in the common case* ("force this node back to master —
harmless if it's already master") can stop being a no-op the moment the system has drifted since
the assumption was written. Before forcing a node back into a role, check the real, current state
of every node that could hold that role — not just the one node you're about to act on — or a
"safe" reclaim can manufacture the exact ambiguous state the rest of the recovery logic is built to
refuse to resolve.

**Concrete instance in this project:** `InfraToggleService.ReclaimRedisMasterRoleAsync` runs as the
first step whenever Redis Sentinel HA is toggled on, unconditionally issuing `REPLICAOF NO ONE`
against the `redis-master` container on the assumption that this is always a safe no-op if
`redis-master` already holds the master role. But if a real Sentinel failover had already promoted
a *different* node (e.g. `redis-replica1`) before this toggle cycle ran — for example, an earlier
ON→OFF→ON cycle that left `redis-master` demoted to a replica of that node — forcing `redis-master`
back to master here would manufacture a *second* master instead of fixing anything.
`RedisInfraService.SelectSoleMaster`, which the very next step in the same toggle-on sequence
(`SelfHealSentinelAsync`) depends on to decide which address every Sentinel instance should point
at, gives up entirely the moment it sees more than one node reporting `ROLE=master` — so this
self-inflicted two-master state would leave every Sentinel instance stuck, possibly pointed at
neither real node, until some unrelated code path happened to resolve it.

⚠️ **Mistake** — `sandbox/infra/control-api/Services/InfraToggleService.cs`
(`ReclaimRedisMasterRoleAsync`, before `383d2d9`):
```csharp
// `REPLICAOF NO ONE` on a node that's already master is a documented no-op, not an error, so
// this is safe to call unconditionally every time Sentinel turns on, not just after a
// confirmed bad state.
private async Task ReclaimRedisMasterRoleAsync(CancellationToken ct)
{
    var container = await runtime.FindAsync("redis-master", ct);
    if (container is null)
    {
        return;
    }

    try
    {
        await runtime.ExecAsync(container.ID, ["redis-cli", "-p", "6379", "REPLICAOF", "NO", "ONE"], ct);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Failed to reclaim redis-master's master role");
    }
}
```

✅ **Do this instead** — check the real topology (every node's own `ROLE`, not Sentinel's belief)
before touching anything, and skip the reclaim entirely if some other node already holds the role
unambiguously:
```csharp
private async Task ReclaimRedisMasterRoleAsync(CancellationToken ct)
{
    var container = await runtime.FindAsync("redis-master", ct);
    if (container is null)
    {
        return;
    }

    var topology = await redis.GetRedisTopologyAsync(ct);
    var otherMaster = topology.Roles.FirstOrDefault(r => r.ServiceId != "redis-master" && r.Role == "master");
    if (otherMaster is not null)
    {
        // That other node is already the unambiguous master SelfHealSentinelAsync needs to
        // anchor on - reclaiming redis-master here would create a second one instead.
        logger.LogWarning(
            "Skipping redis-master's REPLICAOF NO ONE - {OtherServiceId} is already reporting ROLE=master",
            otherMaster.ServiceId);
        return;
    }

    // Still safe to call unconditionally here (including when redis-master is already master
    // itself) - a failure is caught by the container-not-found check that runs right after anyway.
    try
    {
        await runtime.ExecAsync(container.ID, ["redis-cli", "-p", "6379", "REPLICAOF", "NO", "ONE"], ct);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Failed to reclaim redis-master's master role");
    }
}
```

This is a *control-plane* ordering bug, specific to the sandbox's own toggle sequencing — distinct
from (and complementary to) the data-plane split-brain fencing already in place on every Redis
container itself (`--min-replicas-to-write`/`--min-replicas-max-lag`, see the related pitfall
below). That fencing stops an isolated node from accepting writes; this fix stops the control
plane from manufacturing a second master in the first place. Neither replaces the other.

(see `.notes/feature-docs/2026-10-16-1213-security-audit-followup-fixes.md`; found by an external
security audit, not `feature-review` — fixed in `383d2d9`)

## Relatives

### Nodes

- [Redis (master)](node:redis-master) — the node this reclaim step targets
- [Redis Sentinel 1](node:redis-sentinel-1) — the toggle sequence this step is the first step of,
  and whose `SelfHealSentinelAsync` depends on this fix to see an unambiguous topology

### Patterns

- [High availability & failover](pattern:high-availability-failover)

### Pitfalls

- [redis-master accepted writes with no fencing against a split brain](pitfall:redis-master-no-split-brain-fencing)
  — the sibling data-plane fencing: that one stops an isolated node from writing, this one stops
  the control plane from creating a second master to begin with
