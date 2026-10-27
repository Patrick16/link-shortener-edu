# An idempotency claim's TTL has to outlast the slowest real call it guards, not an assumed one

**Category:** config-tuning **Status:** fixed

Watch for this any time an "in-flight" marker (a short-TTL claim meant to distinguish "still
working on it" from "crashed, safe to retry") is sized against an assumed request duration instead
of the slowest path that actually reaches it. If any call site behind the claim can legitimately
run longer than the TTL, the claim expires out from under a request that's still genuinely in
flight — and a retry that should get `409`/replay instead starts a second, concurrent execution of
the same logical operation, exactly the failure mode the claim exists to prevent.

**Concrete instance in this project:** `RedisIdempotencyStore.TryClaimAsync` claims a key at a
short in-flight TTL before the real work runs, so a crashed request's claim self-expires instead of
blocking retries for the full 24h completed-record TTL. The first version set that TTL to 30
seconds — a reasonable-looking number in isolation, but `POST /Links` has two paths that can
legitimately run past it: `CreateWithAliasAsync` (custom aliases) always makes a synchronous gRPC
call to `ShortenerService` with no deadline of its own, and so does the plain-hash path when
`Messaging:Mode=grpc`. Their real duration is whatever the downstream dependency takes — including
this sandbox's own chaos tooling (Pumba-injected latency/partition) deliberately pushing it past a
few seconds on purpose, to demonstrate degraded conditions. If the claim expired while the original
request was still genuinely in flight, a legitimate retry would silently start a *second*
concurrent execution instead of getting `409`/replay.

⚠️ **Mistake** — `src/backend/Shared/Infrastructure/IdempotencyStore.cs` (commit `05934eb`):

```csharp
private static readonly TimeSpan InFlightTtl = TimeSpan.FromSeconds(30);
```

✅ **Do this instead** (`05f0754`) — size the TTL against the slowest call site it actually has to
outlast, not a generic "requests are usually fast" assumption:

```csharp
// 120s, not a tighter bound - CreateWithAliasAsync always takes the synchronous gRPC path (and
// so does the plain-hash path under Messaging:Mode=grpc) with no deadline of its own on that
// call, so its real duration is whatever the downstream dependency takes - including this
// project's own chaos tooling (Pumba-injected latency/partition) deliberately pushing it past
// a few seconds. A claim that expires while the original request is still genuinely in flight
// lets a retry start a second, concurrent execution instead of getting 409/replay (found during
// review) - 120s comfortably outlasts the delays this sandbox's own scenarios inject, while
// still bounding how long a crashed request's claim blocks a legitimate retry to something
// reasonable for a demo, not a production SLA.
private static readonly TimeSpan InFlightTtl = TimeSpan.FromSeconds(120);
```

This only reproduces when a request is both slow enough to outlast the TTL *and* retried during
that window — easy to miss in isolated manual testing (a quick local call to a healthy dependency
never gets close to either number) and only surfaces once a slow or degraded dependency and a
retrying client line up, which is exactly the scenario this sandbox's own chaos tooling exists to
create on demand.

**Provenance:** found by `feature-review` on commit `05934eb`, fixed in `05f0754`.

## Relatives

### Nodes

- [LinkApi](node:link-api) — the one place `Idempotency-Key` claims are taken
- [ShortenerService](node:shortener-service) — the always-synchronous gRPC call whose duration
  this TTL has to outlast

### Patterns

- [Custom aliases](pattern:custom-aliases) — the always-synchronous gRPC path that motivated
  widening the TTL
- [Async messaging](pattern:async-messaging) — `Messaging:Mode=grpc` is the second path that can
  run this long
