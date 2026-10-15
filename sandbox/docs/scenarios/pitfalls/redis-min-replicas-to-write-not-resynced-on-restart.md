# A CLI flag baked in on every container start can quietly undo a runtime `CONFIG SET`

**Category:** resilience-gap **Status:** fixed

A value you changed at runtime via an admin command (`CONFIG SET`) is only in effect until the
process restarts, if that same value is also passed as a startup CLI flag by the container's own
entrypoint — the flag always wins again on the next `redis-server` exec, silently reverting
whatever the runtime change set, with no error anywhere. Any toggle that both (a) restarts a
container and (b) separately tracks a runtime-mutable setting on that same container needs to
re-apply the setting on *every* code path that can restart it, not just the one path that happened
to be built alongside the setting in the first place. Watch for this whenever a container's
entrypoint script bakes in a flag that's also exposed as a live-tunable config value — the two
only agree by coincidence unless something explicitly keeps them in sync on every restart.

**Concrete failure scenario in this project:** `redis-master`'s entrypoint always execs
`redis-server --min-replicas-to-write 1 ...` — a split-brain mitigation that refuses writes unless
at least one replica acknowledges them. The Sentinel toggle already relaxes this to `0` when taking
Sentinel (and its replicas) down, and restores it to `1` when bringing them back — but the separate
Redis toggle's own ON path only started the container back up; it never re-applied the value. With
Sentinel already off, turning Redis off then back on alone (without touching Sentinel) restarted
`redis-master`, which re-baked `min-replicas-to-write=1` via its entrypoint — with no replica
connected, every subsequent cache write from `link-api`/`redirect-api` then failed with
`NOREPLICAS Not enough good replicas to write.`, silently breaking the exact "working standalone
cache" state this toggle advertises.

⚠️ **Mistake** — `sandbox/infra/control-api/Services/InfraToggleService.cs`
(`SetRedisEnabledAsync`, as shipped in `6c9bcfc`):

```csharp
if (enabled)
{
    await SyncRedisMasterContainerAsync(true, ct);
    _cacheEnabled = true;
    await RecreateCacheUsingServicesAsync(ct);
}
```

`SetRedisMinReplicasToWriteAsync` existed already — it just only ever got called from the
*Sentinel* toggle's own code path, not from here.

✅ **Do this instead** — re-apply the setting on every path that can restart the container, matching
it to whatever the current (independent) state actually is, rather than trusting the entrypoint's
baked-in default:

```csharp
if (enabled)
{
    await SyncRedisMasterContainerAsync(true, ct);
    // redis-master's entrypoint always bakes in min-replicas-to-write=1 on every restart (a CLI
    // flag, never persisted to disk) regardless of whether Sentinel/replicas are actually up -
    // re-sync it to match the current Sentinel state instead of trusting the just-applied default.
    await SetRedisMinReplicasToWriteAsync(_redisConnectionViaSentinel ? 1 : 0, ct);
    _cacheEnabled = true;
    await RecreateCacheUsingServicesAsync(ct);
}
```

**A second, genuinely separate bug surfaced while fixing the first one.** The exec call this fix
relies on used `ExecAsync`, which never inspects the exit code at all — its own contract is "return
whichever of stdout/stderr has content," not success/failure. Right after
`SyncRedisMasterContainerAsync(true, ct)` starts the container, `redis-server` can legitimately take
close to 10 seconds to actually start accepting connections (the entrypoint's own Sentinel-discovery
DNS/retry logic tries each of 3 Sentinel hosts first) — a `redis-cli` call issued immediately after
just prints a connection error, which `ExecAsync` happily returns as if it were the command's normal
output. The fix above *looked* like it worked in the logs while `min-replicas-to-write` silently
stayed at the entrypoint's default the whole time:

```csharp
// before - "succeeded" in the logs regardless of whether redis-cli actually connected
try
{
    await runtime.ExecAsync(container.ID,
        ["redis-cli", "CONFIG", "SET", "min-replicas-to-write", value.ToString()], ct);
    logger.LogWarning("Set redis-master min-replicas-to-write={Value}", value);
}
catch (Exception ex)
{
    logger.LogWarning(ex, "Setting redis-master min-replicas-to-write={Value} failed", value);
}
```
```csharp
// after - a non-zero exit code is a real signal now, retried with the container's own realistic
// startup delay in mind (20 attempts, 1s apart)
const int maxAttempts = 20;
for (var attempt = 1; attempt <= maxAttempts; attempt++)
{
    var (exitCode, output) = await runtime.ExecWithExitCodeAsync(container.ID,
        ["redis-cli", "CONFIG", "SET", "min-replicas-to-write", value.ToString()], ct);
    if (exitCode == 0)
    {
        logger.LogWarning("Set redis-master min-replicas-to-write={Value} (attempt {Attempt}/{MaxAttempts})", value, attempt, maxAttempts);
        return;
    }
    if (attempt < maxAttempts) await Task.Delay(TimeSpan.FromSeconds(1), ct);
}
logger.LogWarning("Setting redis-master min-replicas-to-write={Value} failed after {MaxAttempts} attempts", value, maxAttempts);
```

A helper method that swallows an exception and merely logs a warning (a deliberate, documented
choice here — a transiently unreachable container must not abort a toggle that already stopped/
started real resources) is only actually best-effort if it can tell success from failure in the
first place. `ExecAsync`'s "whichever stream has content" contract can't.

(see review-reports/2026-10-16-0240-redis-sentinel-toggle.md, F1 — fixed in `ae134b6`)

## Relatives

### Nodes

- [Redis (master)](node:redis-master) — the container whose entrypoint re-bakes this flag on every restart
- [Redis Sentinel 1](node:redis-sentinel-1) — the toggle this value has to stay in sync with

### Patterns

- [High availability & failover](pattern:high-availability-failover) — the split-brain mitigation this flag implements
