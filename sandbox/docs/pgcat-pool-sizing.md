# Investigation: 200-VU / 20-replica performance collapse

Triggered by a real symptom: scaling `link-api`/`redirect-api` to 20 replicas and running 200 VUs
against `POST /Links` made throughput collapse (iterations/s dropped to near zero for long stretches
while VUs stayed flat) and `/health/ready` p95 sat at ~20 seconds. Below is what actually caused it,
in the order the evidence came in — three separate, real problems were stacked on top of each other,
and fixing the first one just uncovered the next.

## Symptom 1: throughput collapse on `POST /Links`

**Cause:** `pgcat`'s `pool_size` (`sandbox/infra/pgcat/pgcat.toml`, one shared knob for all three
database pools — see `PgcatPoolSettings` in
[`ControlApi/Models/PgcatPoolSettings.cs`](../infra/control-api/Models/PgcatPoolSettings.cs)) was
still at its original default of **10**, unrelated to how many `link-api` replicas were scaled up.
Scaling replicas increases concurrency *in front of* pgcat, not the number of backend connections
pgcat is allowed to hold open — with `pool_mode = "transaction"`, only `pool_size` transactions can
run against a given role at once, so at 200 VUs the other ~190 queued behind those 10 slots. The
control panel's own **Bottleneck Advisor** already has a rule for exactly this
([`BottleneckAdvisor.cs:65-76`](../infra/control-api/Services/BottleneckAdvisor.cs#L65-L76)):
`pgcat.Pools[].ClientWaiting > 0` → *"the pgcat pool is exhausted"*.

**Fix:** raised `pool_size` to **40**
([`DockerService.cs`](../infra/control-api/Services/DockerService.cs)'s `_pgcatPoolSettings`
default and `RenderPgcatToml`, plus the tracked baseline
[`pgcat.toml.example`](../infra/pgcat/pgcat.toml.example) — `pgcat.toml` itself is gitignored
runtime state, regenerated from `.example` on first `start-stack.ps1` run or from `RenderPgcatToml`
on the next pool-settings change). Raising `pool_size` alone would let pgcat try to open up to
`pool_size × 3 pools` connections to the primary (worst case 120) — comfortably over the shipped
Postgres `max_connections=100` with zero headroom for `pgweb`/exporters/admin `psql`, so
`POSTGRES_MAX_CONNECTIONS` was also raised **100 → 200** in
[`docker-compose.yml`](../docker-compose.yml) (3 occurrences: primary's `command:`, and both
replicas' `POSTGRES_MAX_CONNECTIONS` env — a replica's value must be ≥ the primary's or it refuses to
start hot standby).

**Verified:** 200 VUs / 20 replicas, 60s, `POST /Links` only:

| | before | after pool_size+max_connections fix |
|---|---|---|
| failed requests | throughput collapse, long zero-iteration stretches | **0 / 99092 (0%)** |
| `http_req_duration` avg / p95 | — (collapsed) | **119 ms / 260 ms** |
| throughput | near-zero for long stretches | **~1400 req/s sustained** |
| pgcat `clientWaiting` | (not captured live at the time) | **0** throughout |

This was the headline fix — real traffic went from collapsing to fully healthy. Two more problems
were hiding behind it, both isolated to `/health/ready`, not to real traffic.

## Symptom 2 (ruled out): CPU limit

With pgcat fixed, `/health/ready` was still spiking to ~20s. `link-api`'s CPU
(`deploy.resources.limits.cpus`, [`docker-compose.yml`](../docker-compose.yml)) showed
`maxCpuPercent≈99.8%` of its 1.0-core limit during the run, and 20×`link-api` + 20×`redirect-api` at
1.0 core each is 40 nominal cores against a 32-core host — a real oversubscription on paper, and a
plausible story (Docker healthcheck's own `curl` shares the same cgroup quota as the app).

**Tested directly** via `cpu.stat` inside the containers (cgroup v2: `nr_throttled`/`throttled_usec`)
before and after raising the limit **1.0 → 1.5**: throttling counters were essentially unchanged
between a run at 1.0 and a run at 1.5 (e.g. `nr_throttled` stayed at 8 across an entire second load
test — zero new throttled periods), and `/health/ready` p95 was still ~20s at 1.5 cores. **CPU limit
was not the cause** — kept at 1.5 anyway since the extra headroom is harmless and the throttling
counters, while not the culprit here, were real and non-zero at 1.0.

## Symptom 3: Docker-DNS flakiness banning a pgcat replica

`docker logs sandbox-pgcat-1` during a run showed intermittent
`Could not connect to server: failed to lookup address information: Name or service not known`
against `postgres-replica1`/`postgres-replica2`, followed by `Banning instance ... reason:
FailedCheckout` / `FailedHealthCheck`. This is the same class of Docker embedded-DNS flakiness under
connection bursts already known in this project for Redis Sentinel (see
`resilience-ha-postgres-mongo-redis` in project memory) — here it's pgcat's own backend connections
hitting it instead. `pgcat.toml`'s `[general] ban_time` was **20** (seconds): a transient,
sub-second DNS blip was turning into a full 20-second unavailability window for that replica, which
lined up suspiciously well with the observed p95.

**Fix:** `ban_time` **20 → 3** seconds (same two places as `pool_size`: `RenderPgcatToml` in
`DockerService.cs`, and the tracked `pgcat.toml.example`). This genuinely happened and is worth
knowing about, but turned out **not to be the main driver of the ~20s figure** — after this fix
alone, `/health/ready` p95 was still ~20024ms in the next run. Left in regardless: shortening a
circuit-breaker's ban window so a one-off blip doesn't cascade into 20s is correct on its own merits.

## Symptom 4 (the actual remaining cause): `RabbitMqHealthCheck` not bounded by its own timeout

Chasing why the ~20s figure didn't move despite three real fixes, a direct per-second probe against
one container during a run (`docker exec ... curl .../health/ready`, bypassing Docker's own 5s
healthcheck timeout) caught it in the act — and the container's own Serilog output had already said
exactly what was happening:

```
Health check rabbitmq with status Unhealthy completed after 13145.0117ms with message 'RabbitMQ did not respond within 3s.'
```

The health check's own 3-second guard *fired* (that message is this fix's own text — see
[`RabbitMqHealthCheck.cs`](../../src/backend/Shared/Infrastructure/RabbitMqHealthCheck.cs)) — but the
check still took 13+ seconds wall-clock to actually return. Root cause:
[`RabbitMqClient.CreateChannelAsync`](../../src/backend/Shared/Infrastructure/RabbitMqClient.cs)
routes through `GetConnectionAsync`, which shares **one** connection-establishment `Task` across every
caller in the process (deliberately — see that file's own comments on why a failed attempt isn't
cached per-caller). If that shared task is still connecting when a caller with a shorter deadline
comes along, `await existing!` just awaits it — the awaited `Task` was never given *this* caller's
`CancellationToken`, so cancelling it doesn't cancel that task; RabbitMQ.Client's own
`CreateConnectionAsync` also doesn't reliably abort an in-flight handshake just because a token fired.
A `CancellationToken` passed into `CreateChannelAsync` bounds nothing when the wait is on a `Task`
that was already running before you started watching it.

**Fix:** race the call against a `Task.Delay(Timeout)` with `Task.WhenAny` instead of relying on
cancellation alone — this bounds *this caller's* wait without touching the shared connection attempt,
which keeps running in the background for whoever (a real publisher/consumer) actually needs it to
eventually succeed. Applied only to
[`RabbitMqHealthCheck.cs`](../../src/backend/Shared/Infrastructure/RabbitMqHealthCheck.cs) — the DB
health checks (`DbContextHealthCheck<T>`/`DbContextFactoryHealthCheck<T>` in
[`DbContextHealthCheck.cs`](../../src/backend/Shared/Infrastructure/DbContextHealthCheck.cs)) were
never actually slow in the logs once pgcat was fixed (EF's `CanConnectAsync` consistently 1-30ms), so
they keep the simpler `CancellationTokenSource.CancelAfter` form. `RabbitMqPublisher`'s own publish
path was deliberately left alone — a real publish *should* wait for a working connection rather than
fail fast (it already has a SQLite fallback + retry worker for genuine broker outages); racing it
would trade "occasionally slow" for "occasionally a request that could have succeeded gets pushed to
the fallback queue instead," which is a different, bigger behavior change than what this investigation
was asked to fix.

## Final verified numbers (200 VUs, 20 `link-api` + 20 `redirect-api` replicas, 60s, `POST /Links`)

| metric | original (all 4 problems present) | final (all fixes applied) |
|---|---|---|
| failed requests | throughput collapse | **0 / 89321 (0%)** |
| `http_req_duration` p95 | — (collapsed) | **246 ms** |
| `link-api GET /health/ready` p95 / max | **~20069 ms / ~21066 ms** | **~3060 ms / ~9811 ms** |
| `redirect-api GET /health/ready` p95 / max | **~20016 ms / ~20144 ms** | **~3022 ms / ~10365 ms** |
| pgcat `clientWaiting` | not captured (already exhausted) | 0, one transient 1-client blip, no failures caused |

`/health/ready`'s remaining ~9-10s `max` (vs. the ~3s the code now targets) is occasional scheduling
jitter under peak load, not a return of the original problem — nowhere near the original ~20-55s
figures, and Docker's own healthcheck (`interval: 5s, timeout: 5s, retries: 10`,
[`docker-compose.yml`](../docker-compose.yml)) tolerates it without flapping the container unhealthy.

## Files changed

- [`sandbox/infra/pgcat/pgcat.toml.example`](../infra/pgcat/pgcat.toml.example) — `pool_size` 10→40,
  `ban_time` 20→3 (the tracked baseline; live `pgcat.toml` is gitignored runtime state)
- [`sandbox/infra/control-api/Services/DockerService.cs`](../infra/control-api/Services/DockerService.cs)
  — `_pgcatPoolSettings` default 10→40, `RenderPgcatToml`'s `ban_time` 20→3 (kept in sync with
  `.example` so `GetPgcatPoolSettings()`/a fresh `pgcat.toml` don't drift back to the old values)
- [`sandbox/docker-compose.yml`](../docker-compose.yml) — `POSTGRES_MAX_CONNECTIONS` default
  100→200 (3 occurrences); `link-api`/`redirect-api` `cpus` limit 1.0→1.5 (kept as harmless headroom,
  not the actual fix — see Symptom 2)
- [`src/backend/Shared/Infrastructure/DbContextHealthCheck.cs`](../../src/backend/Shared/Infrastructure/DbContextHealthCheck.cs)
  — both health checks bounded to a 3s `CancellationTokenSource.CancelAfter` so a saturated pool fails
  the probe fast instead of hanging past Docker's own 5s timeout
- [`src/backend/Shared/Infrastructure/RabbitMqHealthCheck.cs`](../../src/backend/Shared/Infrastructure/RabbitMqHealthCheck.cs)
  — `Task.WhenAny`-based 3s race (the actual fix for the persistent ~20s figure — see Symptom 4)
- [`src/backend/Shared/Infrastructure/MongoHealthCheck.cs`](../../src/backend/Shared/Infrastructure/MongoHealthCheck.cs)
  — same 3s `CancelAfter` bound, for consistency (not exercised by this specific investigation, no
  Mongo traffic in this scenario)

## What this means for scenario 5 (not built yet)

Scenario 5's own plan (`.notes/PLAN.md`) already calls for "3× pgcat behind a TCP load balancer,
demonstrating correct pool_size sizing" and a TODO to "calculate and document the real
`pool_size × instances` relative to Postgres `max_connections`" — this investigation is effectively
that calculation for the *current* single-pgcat setup: `pool_size × 3 database pools` must stay under
`max_connections` with real headroom for non-pgcat connections, and that math needs to be redone
per-instance once scenario 5 adds more pgcat instances in front of the same Postgres cluster.
