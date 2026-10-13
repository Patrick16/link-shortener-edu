# A cached "is it running" flag went stale the instant a second, unguarded control path touched the same container

**Category:** concurrency **Status:** fixed

A standing toggle that mirrors real container state (not just an env var) is tempting to back with a
plain in-memory bool, written wherever the toggle's own code starts/stops the container. That's safe
only if the toggle's own gated path is the *only* way that container's state ever changes. In a
system that already gives every node generic Start/Stop controls for free — exactly what this
project's `ContainerEndpoints`/`ServiceControls.tsx` does for every container, independent of any
component-specific capability — that assumption is false by construction: the generic path calls the
same Docker API, on the same container, with no allowlist and no knowledge that a specific toggle
also claims to own that container's state. The moment anyone uses the generic Stop/Start button on a
node that also has its own standing toggle, the cached bool silently stops reflecting reality, and
every fail-safe guard built on top of that bool (here: "don't let haproxy report OFF while pgcat
depends on it" and "don't let haproxy report ON while pgcat is disabled") now protects against a
state that doesn't exist, while the actually-dangerous state goes unflagged. Watch for this any time
a new standing toggle wraps a container that *also* sits on a generic/shared control surface — the
two paths will fight over the same resource unless the "current state" is derived from the resource
itself rather than cached by only one of them.

⚠️ **Mistake** — `sandbox/infra/control-api/Services/InfraToggleService.cs` (as shipped in `4c789d3`):

```csharp
private bool _haproxyEnabled = true;

public InfraStatus GetInfraStatus() =>
    new(_nginxBypassed, _pgcatEnabled, _cacheEnabled, _messagingMode, _haproxyEnabled);

private async Task SyncHaproxyContainerAsync(bool enabled, CancellationToken ct)
{
    var container = await runtime.FindAsync("haproxy", ct)
        ?? throw new InvalidOperationException("haproxy container not found");

    if (enabled)
    {
        await runtime.Client.Containers.StartContainerAsync(container.ID, new ContainerStartParameters(), ct);
    }
    else
    {
        await runtime.Client.Containers.StopContainerAsync(container.ID, new ContainerStopParameters { WaitBeforeKillSeconds = 10 }, ct);
    }

    _haproxyEnabled = enabled;
}
```

The exact same `IContainerRuntime.FindAsync` + `Containers.Start/StopContainerAsync` primitives are
also reachable, for `serviceId = "haproxy"`, through the pre-existing generic
`POST /api/containers/{serviceId}/stop` / `/start` endpoints (`ContainerEndpoints.cs`) that every node
panel renders via `ServiceControls.tsx` — including haproxy's own panel, right next to the new
toggle. Those calls never touch `_haproxyEnabled`, so clicking the generic Stop button leaves the
field reporting `true` for a container that is actually stopped (and the standalone toggle's own
`enabled && !_pgcatEnabled` guard — the one rule this feature exists to enforce — is reachable only
through `SetHaproxyEnabledAsync`, not through the generic path at all).

✅ **Do this instead** — don't cache it; read the container's real state live on every status request,
so there is nothing for a second mutation path to leave stale:

```csharp
// See InfraStatus's own comment on HaproxyEnabled for why that one field is read live here
// instead of joining _nginxBypassed/_pgcatEnabled/_cacheEnabled/_messagingMode as cached state.
public async Task<InfraStatus> GetInfraStatusAsync(CancellationToken ct) =>
    new(_nginxBypassed, _pgcatEnabled, _cacheEnabled, _messagingMode, await IsHaproxyRunningAsync(ct));

private async Task<bool> IsHaproxyRunningAsync(CancellationToken ct) =>
    await runtime.FindAsync("haproxy", ct) is { State: "running" };

// SyncHaproxyContainerAsync no longer writes back to any field - there is nothing left to
// keep in sync; every reader asks Docker directly instead of trusting a flag.
```

This closes the gap for every current and future path that can flip the container — the standing
toggle's own gated call, the generic per-node Start/Stop, and anything added later — with nothing
left to keep in sync. The trade-off is explicit, not free: every `/api/infra/status` read now costs
one extra Docker API round-trip, acceptable for this sandbox-scale tool's polling frequency but worth
remembering if that frequency ever goes up.

(see review-reports/2026-10-14-0205-haproxy-toggle.md, F2 — fixed in `7427655`)

## Relatives

### Nodes

- [HAProxy](node:haproxy) — the container both control paths touch
- [PgCat](node:pgcat) — the dependency `SetHaproxyEnabledAsync`'s guard protects

### Patterns

- [Pooler scaling](pattern:pooler-scaling) — the pattern this toggle demonstrates a failure mode for
