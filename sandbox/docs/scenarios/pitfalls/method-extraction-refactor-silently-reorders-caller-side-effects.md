# Extracting a method verbatim doesn't guarantee the new orchestrator calls it at the same point in the sequence

**Category:** architecture-bug **Status:** fixed

Watch for this specifically when a refactor's commit message says something like "pure move, no
logic change" — it's an easy claim to believe and a surprisingly easy one to get subtly wrong. If
every individual extracted method's body is verified byte-for-byte identical to its pre-move
source, it's tempting to conclude the refactor changed nothing observable. But a method body being
unchanged says nothing about *when* the new orchestrator calls it relative to everything else it
now also calls through different collaborators. If the original class called step A then step B
inline, and the split buries step A's call site inside a new helper class that the new orchestrator
happens to invoke *after* step B instead of before, every line of A and B is still individually
correct — the bug lives entirely in the orchestrator's new call order, which a method-body diff
will never catch.

**Concrete instance in this project:** control-api's `TrafficService` (the orchestrator behind a k6
load-test run) used to call `_runtime.EnsureImageAsync(K6Image, ct)` — check/pull the k6 Docker
image — as literally its first real action, before reading the nginx-bypass flag and running the
(potentially slow, real-HTTP) data-pool preload. When the 698-line class was split into
`K6ContainerRunner` (owns the Docker container lifecycle) and `DataPoolFetcher` (owns the preload),
the image check moved along with the rest of the container-lifecycle code into
`K6ContainerRunner.RunAsync`'s first line — a byte-for-byte faithful move of that specific logic.
But `TrafficService.RunTrafficAsync`, the new slim orchestrator, now called the preload *first* and
only called into `K6ContainerRunner.RunAsync` (and therefore the image check) *after* it. Every
individual method's body was unchanged; only the caller's own sequence shifted. On a cold
environment (image never pulled before), this meant the preload's real-HTTP work now ran first,
and a genuinely missing image no longer failed fast — it surfaced only after time had already been
spent on the preload. Caught by a review pass that diffed the *before/after call order*, not by
either the new unit tests (which don't and can't, without Docker, exercise this ordering) or the
build.

⚠️ **Mistake** — `sandbox/infra/control-api/Services/TrafficService.cs` (as split in `faf34ba`):
the image check now lives entirely inside the helper, called only once the preload is already done:

```csharp
// TrafficService.RunTrafficAsync, post-split:
var nginxBypassed = (await _infraToggle.GetInfraStatusAsync(ct)).NginxBypassed;

// Real-HTTP preload now runs BEFORE the k6 image is ever checked/pulled - a cold environment
// spends this time before discovering the image is missing.
string? poolVar = null;
IReadOnlyList<string>? pool = null;
if (request.DataPool is { Count: > 0 } poolRequest)
{
    // ... pool = await _dataPoolFetcher.FetchAsync(...);
}

// ... eventually, deep inside this call, K6ContainerRunner.RunAsync's own first line is
// EnsureImageAsync - too late to fail fast.
var report = await _containerRunner.RunAsync(request, cmd, env, nginxBypassed, onProgress, ct);
```

✅ **Do this instead** (`d9e3b25`) — expose the image check as its own method and have the
orchestrator call it explicitly, at the same point in the sequence the pre-split code did, before
handing off to the helper for everything else:

```csharp
// K6ContainerRunner.cs
// Exposed so TrafficService can pull/check the image before the (potentially slow) data-pool
// preload, matching the pre-split ordering - RunAsync below still checks it again right before
// actually using it, but EnsureImageAsync no-ops once the image is already present, so the
// repeat call is cheap, not a double pull.
public Task EnsureImageReadyAsync(CancellationToken ct) => runtime.EnsureImageAsync(K6Image, ct);

// TrafficService.cs
// Checked/pulled here, before the data-pool preload below, so a missing image fails fast on a
// cold environment instead of only surfacing after the preload has already spent time hitting
// the real app (restores the pre-split ordering).
await _containerRunner.EnsureImageReadyAsync(ct);

var nginxBypassed = (await _infraToggle.GetInfraStatusAsync(ct)).NginxBypassed;
// ... preload, then _containerRunner.RunAsync as before.
```

The reusable lesson: when splitting one class's inline sequence across several new collaborators,
diff the *orchestrator's own call order* against the original, not just each extracted method's
body — a verbatim method move can still change when a side effect happens relative to everything
else, and that's invisible to both a method-level diff and any test that doesn't exercise the full
Docker-coupled path.

(see `.notes/review-reports/2026-11-05-1418-control-api-traffic-service-split.md`, F1 — recategorized
here from that report's own `logic-bug` label, since the concrete consequence (losing the fail-fast
guarantee on a missing Docker image, and doing avoidable real-HTTP work before discovering it) is a
structural refactoring-risk lesson this project's pitfall docs exist to preserve, not a plain wrong-
output bug — fixed in `d9e3b25`)

## Relatives

### Nodes

- [k6](node:k6) — the container whose image-readiness check lost its fail-fast position (no
  node-doc exists for k6 yet, so this pitfall is currently standalone — not linked from any
  node/pattern doc's own Pitfalls list)
