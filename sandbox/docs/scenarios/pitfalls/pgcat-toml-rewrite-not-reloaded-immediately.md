# Rewriting a config file a process polls on a timer is not the same as that process having read it

**Category:** race-condition **Status:** fixed

A long-running process that watches a config file via a periodic poll (pgcat's own
`autoreload = 15000`) gives you a *maximum* staleness window, not an average one — right after the
file changes, the process is still running on the old config for up to that whole interval. It's
tempting to build an ordering guarantee on top anyway ("write the new config, *then* stop the thing
the old config pointed at — the write happened first, so it must already be in effect"), but the
ordering of two independent writers never closes a gap that only the *reader's own* poll interval
controls. Watch for this any time a control plane mutates both a config file and the real resource
it describes, and assumes "I wrote the file first" is itself a synchronization guarantee.

**Concrete failure scenario in this project:** the Postgres read-replicas toggle's OFF path writes
`pgcat.toml` (dropping `postgres-replica1`/`postgres-replica2` from the `servers` list) and then
stops both containers — the code comment at the time claimed this ordering meant "pgcat has already
stopped routing to them by the time they disappear." It hadn't: pgcat's 3 running instances each
hold their own in-memory copy of the config, re-read only on their own 15-second `autoreload` timer,
completely independent of when the file on disk changed. For up to that whole window, pgcat could
still route a live `SELECT` to a replica whose container had already stopped.

⚠️ **Mistake** — `sandbox/infra/control-api/Services/PgcatService.cs` (as shipped before `0f66b49`):

```csharp
private async Task WritePgcatTomlAsync(PgcatPoolSettings settings, bool replicasEnabled, CancellationToken ct)
{
    var path = Path.Combine(_pgcatConfigDir, "pgcat.toml");
    await File.WriteAllTextAsync(path, RenderPgcatToml(settings, replicasEnabled), ct);
}
```

The file write is real and immediate; nothing here (or anywhere calling this method) ever tells any
of the 3 running `pgcat` instances to actually look at it sooner than their own timer already would.

✅ **Do this instead** — reach for the consumer's own "reload now" primitive instead of waiting out
its poll interval, on every instance that holds an independent copy:

```csharp
private async Task WritePgcatTomlAsync(PgcatPoolSettings settings, bool replicasEnabled, CancellationToken ct)
{
    var path = Path.Combine(_pgcatConfigDir, "pgcat.toml");
    await File.WriteAllTextAsync(path, RenderPgcatToml(settings, replicasEnabled), ct);
    await ReloadAllInstancesAsync(ct);
}

// pgcat's own admin-console `RELOAD` command picks up pgcat.toml immediately. Every one of the 3
// instances needs its own call - each has an independent in-memory config despite sharing the one
// mounted file. Best-effort: a transient exec failure here must not fail the whole toggle: the
// file write already succeeded, and pgcat's own autoreload timer is still running regardless, so a
// swallowed failure here only widens the window back to "at most ~15s" instead of a hard failure.
private async Task ReloadAllInstancesAsync(CancellationToken ct)
{
    foreach (var container in await _runtime.ListAsync("pgcat", ct))
    {
        try
        {
            await _runtime.ExecAsync(container.ID,
                ["sh", "-c", "PGPASSWORD=admin_pass psql -h 127.0.0.1 -p 6432 -U admin_user pgcat -c RELOAD"], ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reloading pgcat instance {ContainerId} failed - autoreload will pick it up within ~15s", container.ID);
        }
    }
}
```

Note the fix is still only best-effort, not a hard guarantee — if the `RELOAD` exec itself fails
(a transient Docker hiccup, or a pgcat instance briefly unreachable), the code silently falls back
to the original ~15s autoreload window. The race is made very unlikely in practice, not made
impossible.

(see review-reports/2026-10-15-2305-postgres-replicas-toggle.md, F1 — fixed in `0f66b49`)

## Relatives

### Nodes

- [PgCat](node:pgcat) — the component whose 3 instances each needed the explicit reload
- [Postgres Replica 1](node:postgres-replica1) — the container this toggle stops/starts around the write

### Patterns

- [Read-replica routing](pattern:read-replica-routing) — the toggle this race lives in demonstrates
  this pattern degrading/recovering live
