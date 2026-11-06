# Click counter incremented before the dispatch it should depend on

**Category:** resilience-gap **Status:** fixed

When a request does two things that aren't naturally atomic — bump a counter, then hand the event
off to whatever persists it for real — the order matters even when it looks cosmetic. Incrementing
the "cheap" side effect first, on the assumption the "real" work almost never fails, bakes in a
hidden coupling to the downstream call's reliability — one that turns real the moment that call's
failure characteristics change, even though the counter's own code never did.

**Concrete instance in this project:** `RedirectController.RedirectToOrigin` increments the Redis
click counter (`IClickCounterService`) and dispatches the `ClickTrackedEvent`
(`IEventDispatcher.DispatchAsync`) on every redirect. In the default async/RabbitMQ mode, the
dispatch essentially never throws — it just enqueues. Once messaging-mode=grpc existed
(`SyncGrpcDispatcher`, a direct synchronous RPC to TrafficService with no fallback queue), the
dispatch could genuinely fail — e.g. TrafficService unreachable, raising a 502/503 on the whole
redirect. With the counter incremented first and unconditionally, every failed dispatch would still
have bumped the "my links" dashboard's click count for a click that was never actually redirected
or persisted anywhere — permanently drifting the displayed count from reality with no way to
reconcile it.

⚠️ **Mistake** — reconstructed; no diffable pre-fix commit exists (this feature was reviewed and
fixed before its first commit, so there's no separate buggy commit to diff against):

```csharp
// src/backend/Services/RedirectApi/Controllers/RedirectController.cs
await _clickCounter.IncrementAsync(hash, cancellationToken);
await _dispatcher.DispatchAsync(clickEvent, Topics.ClickTracked, cancellationToken);
```

✅ **Do this instead** — increment only after the dispatch has actually succeeded, so a failed
dispatch (possible now that `SyncGrpcDispatcher` has no fallback queue) never leaves the counter
bumped for a click that wasn't recorded:

```csharp
// src/backend/Services/RedirectApi/Controllers/RedirectController.cs
await _dispatcher.DispatchAsync(clickEvent, Topics.ClickTracked, cancellationToken);
await _clickCounter.IncrementAsync(hash, cancellationToken);
```

Harmless in the default async mode; it only starts mattering once a dispatcher implementation
exists that can genuinely fail on the request's own call stack. Not independently live-tested with
failure injection (would need a precisely timed TrafficService outage during a gRPC-mode redirect)
— verified by code reading plus the passing build/test suite instead, per the source report.

(reconstructed from this project's own review notes plus the current code at HEAD — no pre-fix
commit hash to cite since this was fixed before its first commit)

## Relatives

### Nodes

- [RedirectApi](node:redirect-api)
- [Redis](node:redis-master) — backs the click counter
- [TrafficService](node:traffic-service) — the downstream dispatch target that can now fail

### Patterns

- [Async messaging](pattern:async-messaging) — the dispatcher abstraction (`IEventDispatcher`)
  that made this failure mode reachable once a synchronous implementation existed
