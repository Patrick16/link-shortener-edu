# A failed "temporarily change state, then undo it" sequence can throw before the undo ever runs

**Category:** resilience-gap **Status:** fixed

A routine that temporarily mutates shared state in order to force some outcome (here: bumping one
replica-set member's election priority to force it to win the next vote), intending to restore the
original state once that outcome is confirmed, has to handle the case where the outcome never
arrives — not just the happy path where the restore step runs as planned. If the "restore" logic
only lives inside the success branch, a timeout doesn't just fail the current operation, it leaves
the temporary mutation permanently in place, as a silent side effect of an operation that otherwise
failed cleanly (clear exception, gate released, no hang). Watch for this any time a method's retry/
poll loop can exhaust without reaching the code path that was supposed to clean up after it.

**Concrete failure scenario in this project:** the MongoDB replicas toggle's OFF path needs to run
`rs.reconfig()`/`rs.add()` against whichever node is currently `rs0`'s primary — but MongoDB refuses
to let a node reconfigure itself out of the member list, and `mongo1` is the toggle's one permanent
anchor member, so every removal has to run from `mongo1`'s own connection. When `mongo1` isn't
already primary (a normal occurrence — MongoDB elects a primary with no regard for container
naming), `EnsureMongo1IsPrimaryAsync` temporarily bumps `mongo1`'s priority to 2 (and every other
member's to 1), steps the current primary down, and polls up to 15 times for `mongo1` to take over.
The normal success path's own cleanup script resets every member's priority back to 1 as its last
step — but that script only runs *after* this method returns successfully. If the 15-attempt poll
exhausts instead (a stalled election, a flaky network — plausible precisely during the chaos/
degraded scenarios this control plane exists to exercise), the method threw its exception before
ever reaching that reset, leaving `rs0`'s voting configuration permanently skewed: `mongo1` favored
over whichever node used to be primary, discoverable only by someone reading `rs.conf()` by hand.

⚠️ **Mistake** — `sandbox/infra/control-api/Services/MongoTopologyService.cs`
(`EnsureMongo1IsPrimaryAsync`, as shipped in `b42876e`):

```csharp
// ... 15-attempt poll for mongo1 to become primary ...
throw new InvalidOperationException(
    "mongo1 did not become rs0's primary after stepping the previous one down - " +
    "cannot safely remove mongo2/mongo3 without risking a self-removal error on whichever node is still primary");
```

The priority bump and stepdown already happened several lines earlier in this same method — this
is simply the point where it gives up, with nothing undoing them on the way out.

✅ **Do this instead** — treat the timeout path as its own cleanup opportunity, not just an error to
surface; find whatever the *current* state actually is (not necessarily what you expected it to be)
and reset from there before propagating the failure:

```csharp
// The success path's own removeScript resets priorities back to 1 once it's done - this timeout
// means that script never runs, so without an explicit reset here the priority bump above would
// be left permanently skewing every future election, not just this one attempt.
await TryResetMemberPrioritiesAsync(ct);
throw new InvalidOperationException(
    "mongo1 did not become rs0's primary after stepping the previous one down - " +
    "cannot safely remove mongo2/mongo3 without risking a self-removal error on whichever node is still primary");
```

```csharp
// Best-effort cleanup for the timeout path above. Finds whichever node is CURRENTLY primary - not
// necessarily mongo1, since the election this is cleaning up after never confirmed settling on it
// - and resets every member's priority back to the shipped default (1) from there.
private async Task TryResetMemberPrioritiesAsync(CancellationToken ct)
{
    var primaryServiceId = await TryFindPrimaryServiceIdAsync(ct);
    var container = primaryServiceId is null ? null : await runtime.FindAsync(primaryServiceId, ct);
    if (container is null)
    {
        logger.LogWarning("TryResetMemberPrioritiesAsync: no reachable primary to reset rs0's member priorities from - they may stay skewed until a future call fixes this");
        return;
    }

    const string resetScript = """
        var cfg = rs.conf();
        if (cfg.members.some(m => m.priority !== 1)) {
          cfg.members.forEach(m => { m.priority = 1; });
          cfg.version += 1;
          rs.reconfig(cfg);
        }
        """;
    try
    {
        await runtime.ExecAsync(container.ID, ["mongosh", "--quiet", "--eval", resetScript], ct);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "TryResetMemberPrioritiesAsync: failed to reset rs0's member priorities from {ServiceId} - they may stay skewed until a future call fixes this", primaryServiceId);
    }
}
```

This cleanup is itself best-effort (logged, not retried or escalated) — if the node it needs to
reset from is also unreachable at that exact moment, `rs0`'s priorities can still end up skewed,
just far less often than with no rollback attempt at all. A retry of the same toggle afterward is
likely to succeed on its own (the failed attempt already bumped `mongo1`'s priority), but that's a
side effect masking the gap, not evidence the first attempt was harmless.

(see review-reports/2026-10-16-0240-mongo-replicas-toggle.md, F1 — fixed in `ae134b6`)

## Relatives

### Nodes

- [Mongo 1](node:mongo1) — the permanent anchor member whose priority gets bumped

### Patterns

- [High availability & failover](pattern:high-availability-failover) — the election this rollback keeps honest
