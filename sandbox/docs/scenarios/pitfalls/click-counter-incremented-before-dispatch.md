# Click counter incremented before the dispatch it should depend on

**Category:** resilience-gap **Status:** fixed

When a request does two things that aren't naturally atomic — bump a counter, then hand the event
off to whatever persists it for real — the order they happen in matters even when it looks
cosmetic. Incrementing the "cheap" side effect first, on the theory that the "real" work almost
never fails, quietly bakes in an assumption about how reliable the downstream call is. That
assumption can become false the moment the downstream call's failure characteristics change, even
if the counter's own code never does — exactly the kind of coupling that's invisible until the
system around a component changes shape.

**Concrete instance in this project:** `RedirectController.RedirectToOrigin` increments the Redis
click counter (`IClickCounterService`) and dispatches the `ClickTrackedEvent`
(`IEventDispatcher.DispatchAsync`) on every redirect. In the default async/RabbitMQ mode, the
dispatch essentially never throws — it just enqueues. Once messaging-mode=grpc existed as an
alternative (`SyncGrpcDispatcher`, a direct synchronous RPC to TrafficService with deliberately no
fallback queue), the dispatch call could now genuinely fail — e.g. TrafficService unreachable,
raising a 502/503 on the whole redirect. If the counter increment still ran first and
unconditionally, every one of those failed dispatches would have already bumped the "my links"
dashboard's click count for a click that was never actually redirected or persisted anywhere,
permanently drifting the displayed count from reality with no way to reconcile it.

⚠️ **Mistake** — reconstructed; no diffable pre-fix commit exists (this feature was reviewed and
fixed before its first commit — see
`.notes/review-reports/2026-10-06-1430-messaging-toggle-grpc-review.md`, F2):

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

Harmless in the default async mode (`AsyncQueueDispatcher` essentially never throws) — this only
starts mattering once a dispatcher implementation exists that can genuinely fail on the request's
own call stack. Not independently live-tested with failure injection (would need a precisely timed
TrafficService outage during a gRPC-mode redirect) — verified by code reading plus the passing
build/test suite instead, per the source report.

(see `.notes/review-reports/2026-10-06-1430-messaging-toggle-grpc-review.md`, F2 — fixed; no
pre-fix commit hash cited, reconstructed from the finding's own Summary plus the current code at
HEAD)

## Relatives

### Nodes

- [RedirectApi](node:redirect-api)
- [Redis](node:redis-master) — backs the click counter
- [TrafficService](node:traffic-service) — the downstream dispatch target that can now fail

### Patterns

- [Async messaging](pattern:async-messaging) — the dispatcher abstraction (`IEventDispatcher`)
  that made this failure mode reachable once a synchronous implementation existed
