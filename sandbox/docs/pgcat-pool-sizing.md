# Investigation: 200-VU / 20-replica performance collapse

Triggered by a real symptom: scaling `link-api`/`redirect-api` to 20 replicas and running 200 VUs
against `POST /Links` made throughput collapse (iterations/s dropped to near zero for long stretches
while VUs stayed flat) and `/health/ready` p95 sat at ~20 seconds. Below is what actually caused it,
in the order the evidence came in. Despite the title, this ended up covering **eight** distinct,
real problems stacked on top of each other — fixing one just uncovered the next, and a code-review
pass on the fixes themselves (Symptoms 5-6) caught two more before they shipped. Kept the original
filename since every code comment already points at it and scope grew incrementally, not from a
second, unrelated investigation.

## Symptom 1: throughput collapse on `POST /Links`

**Cause:** `pgcat`'s `pool_size` (`sandbox/infra/pgcat/pgcat.toml`, one shared knob for all three
database pools — see `PgcatPoolSettings` in
[`ControlApi/Models/PgcatPoolSettings.cs`](../infra/control-api/Models/PgcatPoolSettings.cs)) was
still at its original default of **10**, unrelated to how many `link-api` replicas were scaled up.
Scaling replicas increases concurrency *in front of* pgcat, not the number of backend connections
pgcat is allowed to hold open — with `pool_mode = "transaction"`, only `pool_size` transactions can
run against a given role at once, so at 200 VUs the other ~190 queued behind those 10 slots. The
control panel's own **Bottleneck Advisor** already has a rule for exactly this
([`BottleneckAdvisor.cs:59-72`](../infra/control-api/Services/BottleneckAdvisor.cs#L59-L72)):
`pgcat.Pools[].ClientWaiting > 0` → *"the pgcat pool is exhausted"*.

**Fix:** raised `pool_size` to **40**
([`PgcatService.cs`](../infra/control-api/Services/PgcatService.cs)'s `_pgcatPoolSettings`
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
`PgcatService.cs`, and the tracked `pgcat.toml.example`). This genuinely happened and is worth
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

## Symptom 5: `RabbitMqHealthCheck`'s `Task.WhenAny` fix leaked the losing channel

Caught by a code-review pass on the fix itself, not by load-test numbers — this one wouldn't show up as latency at all.

When `Task.WhenAny(channelTask, Task.Delay(Timeout, cancellationToken))` picked the delay (RabbitMQ
still slow), the method returned `Unhealthy` immediately and never touched `channelTask` again. If
that task *later* completed successfully — the exact scenario the fix exists for — it handed back a
live `IChannel` that nothing ever disposed: a leaked client-side channel plus a slot in the broker's
per-connection channel table, repeating every 5s (Docker's healthcheck interval) for as long as
RabbitMQ stayed slow.

**Fix:** [`RabbitMqHealthCheck.cs:30-44`](../../src/backend/Shared/Infrastructure/RabbitMqHealthCheck.cs#L30-L44)
— on the losing branch, attach a `ContinueWith` to the abandoned `channelTask` that disposes the
channel if it eventually succeeds, or observes (and discards) the fault if it doesn't, instead of
walking away from it.

## Symptom 6 (minor): `MongoHealthCheck`'s timeout message was dead code

Same review pass, same file class of mistake, lower severity: `MongoClickMetaStore.PingAsync`'s own
unconditional `catch { return false; }` swallows any `OperationCanceledException` a
`CancellationTokenSource.CancelAfter` token would produce *before* it ever reached
`MongoHealthCheck`'s own catch block — so the "MongoDB did not respond within 3s" message could never
actually be shown; a genuine slowdown would just look like the generic "not reachable" message. Not a
functional hang (the 3s bound was still honored), just a misleading message and, separately, a missing
generic `catch (Exception ex)` the sibling health checks all have.

**Fix:** switched `MongoHealthCheck` to the same `Task.WhenAny` race
[`RabbitMqHealthCheck`](../../src/backend/Shared/Infrastructure/RabbitMqHealthCheck.cs) uses (bounds
wall-clock regardless of what the inner call does with cancellation, so the timeout message is
reachable) and added the missing generic catch, in
[`MongoHealthCheck.cs`](../../src/backend/Shared/Infrastructure/MongoHealthCheck.cs).

## Symptom 7: the same `RabbitMqClient` cold-start tax, now hitting real `redirect-api` traffic

Verifying Symptom 4's fix (redeployed, fresh 20-replica containers, this time driving
`redirect-api.resolve` instead of `link-api.create`) surfaced the *same* root cause from Symptom 4
again, but on the publish side rather than the health check: `redirect-api`'s `click.tracked publish`
hop (fired from `RedirectController` before every redirect — see
[`docs/scenarios/02-async.md`](scenarios/02-async.md)) showed a single ~57.7s outlier alongside an
otherwise-healthy run. `RabbitMqHealthCheck`'s fix only ever bounded *health checks*'
wait — a real publish call hitting the exact same shared, still-connecting `_connection` task
(`RabbitMqClient.GetConnectionAsync`) has no such bound, deliberately (see Symptom 4's own reasoning
for why racing a real publish would just trade "slow" for "silently dropped to the fallback queue").

**Fix, this time at the actual root cause instead of bounding a symptom:** a new
[`RabbitMqWarmupService`](../../src/backend/Shared/Infrastructure/RabbitMqWarmupService.cs)
(`IHostedService`) that calls `CreateChannelAsync` once during startup, before Kestrel starts
accepting connections — paying the one-time connection-establishment cost during container boot
(when docker-compose's `depends_on: rabbitmq: condition: service_healthy` already guarantees the
broker is up) instead of on whichever real request happens to be first or unlucky. Registered in both
`LinkApi` and `RedirectApi`'s `Program.cs` (the only two services that only *publish* — worker
services' `RabbitMqConsumer` already forces an early connection just by starting to consume, so they
never had this gap).

## Symptom 8: the same cold-start tax again, this time on Redis/Sentinel

Verifying Symptom 7's fix (RabbitMQ warmed, so `click.tracked publish` was fast again) surfaced a
*third* instance of the identical pattern, one layer deeper: `redirect-api`'s `GET {hash}` hop itself
— the actual link resolution, via `LinkCacheService`'s `IDistributedCache` (Redis) with a Postgres
fallback — averaged **~8s** (p95 ~45s, max ~67s) across nearly every one of a fresh run's requests,
with 43 of 1359 requests failing outright. Re-probing the *same*, now-warm replicas a few minutes
later came back in under 5ms — the exact signature of a connection-establishment tax paid by real
traffic instead of at startup, this time for Redis Sentinel discovery (`IDistributedCache` backing
`LinkCacheService`, and a separate raw `IConnectionMultiplexer` `RedirectApi` registers directly for
click-count `INCR`) rather than RabbitMQ.

**Fix:** the same warmup pattern as Symptom 7, in a new
[`RedisWarmupService`](../../src/backend/Shared/Infrastructure/RedisWarmupService.cs) — does one
`IDistributedCache.GetAsync` at startup, plus a `PING` against `IConnectionMultiplexer` if the service
registers one (resolved optionally via `IServiceProvider.GetService`, since only `RedirectApi` has
that second registration — `LinkApi` only has `IDistributedCache`). Registered in both `LinkApi` and
`RedirectApi`.

**Verified:** 200 VUs, 20 `redirect-api` replicas, 60s, `redirect-api.resolve`, freshly-recreated
(cold) containers both times:

| | before (Symptom 7 fixed, Symptom 8 not yet) | after both fixes |
|---|---|---|
| failed requests | 43 / 1359 (3.16%) | **0 / 118269 (0%)** |
| throughput | ~20 req/s | **~1750 req/s** |
| `GET {hash}` avg / p95 / max | **7962 ms / 45451 ms / 66833 ms** | **70 ms / 104 ms / 57779 ms\*** |
| `click.tracked publish` avg / p95 | (already fixed by Symptom 7) | 47 ms / 4.7 ms |

\* One single outlier out of 118268 requests (0.0008%), not a failure — see Symptom 7's own residual
tail; not chased further given how rare it is and that this investigation was already three layers
deep into "which shared connection is cold this time."

## Symptom 9: `/health/ready`'s residual ~3s floor was the health check itself, not scheduling jitter

Symptom 4 shipped `RabbitMqHealthCheck` bounded to a 3s `Task.WhenAny` race and chalked the
post-fix numbers (`~3060ms`/`~3022ms` p95 in the Final verified numbers table below) up to "the
3s the code now targets" plus "occasional scheduling jitter" for the `max` tail. That framing was
wrong: a live run months later (same symptom, a fresh trace table) showed `/health/ready` p95
landing within a few percent of exactly 3000ms on *every* run, not just under peak jitter — too
consistent to be jitter. The real cause: the check opened (`connection.CreateChannelAsync`) and
immediately disposed a brand-new AMQP channel on **every single probe**, Healthy or not. Once
under any load that keeps `RabbitMqClient`'s shared connection busy enough that a fresh
`CreateChannelAsync` call has to queue behind it even briefly, that round trip reliably lands on
the check's own `Timeout` instead of completing in the low milliseconds a channel-open normally
takes on an already-open connection — so the "3s target" wasn't a floor the check settled at under
contention, it was the check paying its own worst case almost every time.

**Fix:** added `IRabbitMqConnection.IsOpen` — a cheap property reading the already-cached
connection's `IConnection.IsOpen` (the exact same signal `RabbitMqClient.IsUsable` already trusts
to tell "still good" from "was good, now dead") with no network round trip at all. `RabbitMqHealthCheck`
checks it first and returns `Healthy` immediately when true, only falling back to the
`Task.WhenAny`-bounded `CreateChannelAsync` race from Symptom 4 when the connection isn't
currently open (cold start before `RabbitMqWarmupService` finishes, or mid-reconnect after a
broker restart).

Separately, `LinkApi`'s `CreateLink` and `RedirectApi`'s `RedirectToOrigin` were awaiting
`IMessagePublisher.PublishAsync` synchronously before responding (the code comment in
`RedirectController` literally said *"Same pattern as LinkApi"* — accurate, just not the fix
Symptom 7 implied it needed: the warmup service solved the one-time cold-start tax, but a
request could still queue behind the *shared* connection any time RabbitMQ was genuinely busy or
mid-reconnect, same root mechanism as Symptom 4, now hitting real traffic instead of a probe).
Added [`LocalPublishQueue`](../../src/backend/Shared/Infrastructure/LocalPublishQueue.cs) — a
bounded (4096-slot), backpressured in-memory channel — and
[`LocalPublishQueueWorker`](../../src/backend/Shared/Infrastructure/LocalPublishQueueWorker.cs), a
pool of 8 background workers that drain it and call the same `IMessagePublisher.PublishAsync`
(RabbitMQ, falling back to the same SQLite store on failure — nothing about that path changed)
off the request's critical path. `LocalPublishQueueWorker.StopAsync` completes the queue's writer
and waits (bounded by the host's own shutdown timeout) for workers to drain whatever was already
queued before the process actually exits, so a graceful shutdown doesn't silently drop in-flight
events — only a hard kill between enqueue and drain still can, the same risk every in-memory
buffer carries; RabbitMQ actually being down is still covered end-to-end by the unchanged SQLite
fallback + `RabbitMqRetryWorker`.

One subtlety this surfaced: `Activity.Current` can't be set back to an `Activity` that has
already `Stop()`'d (the .NET runtime rejects it), and ASP.NET stops the request's `Activity` once
the response finishes — well before a background worker gets around to the queued publish. Simply
carrying the `Activity` object through the queue and restoring `Activity.Current` from a worker
(the first version of this fix) silently lost the trace parent every time, because by the time a
worker tried to restore it, it was already stopped. `IMessagePublisher.PublishAsync` gained an
optional `ActivityContext? parentContext` parameter instead — a plain value, captured from
`Activity.Current?.Context` at enqueue time, with none of the Activity lifecycle's restrictions —
and `LocalPublishQueue` passes it through explicitly so `RabbitMqPublisher` can hand it to
`StartActivity` as the publish span's parent regardless of how long it sat queued.
`RabbitMqRetryWorker`'s own retries intentionally keep `parentContext: null`: a message that fell
back to SQLite already lost its live trace context the moment it was persisted as a
`FallbackMessage` (no trace fields on that record), so a retry minutes or hours later has no
real parent to attach to anyway.

**Verified:** 150 VUs, 60s, default replica counts (5× `redirect-api`, 5× `shortener-service`,
1× `link-api`), `link-api.create` → `redirect-api.resolve`:

| | before (Symptoms 1-8 fixed, this one not) | after |
|---|---|---|
| `link-api GET /health/ready` p95 / max | ~3052 ms / ~3211 ms | **~54 ms / ~54 ms** (13 samples) |
| `redirect-api GET /health/ready` p95 / max | ~3206 ms / ~3801 ms | **~2.8 ms / ~12.5 ms** |
| `redirect-api GET {hash}` p95 / max | ~134 ms / ~11764 ms | **~12 ms / ~662 ms** |

"Before" is the exact trace table a live run produced on this stack prior to this fix (same
shared connection, same `RabbitMqHealthCheck`/synchronous-publish code Symptom 4/7 left in place).
Link-api's own `/health/ready` sample count is low (13, vs. redirect-api's 62) simply because it
runs at 1 replica against redirect-api's 5 in this stack's current scaling — still two orders of
magnitude off the pre-fix number. Zero warnings or errors logged by either service across the run.

## Final verified numbers (200 VUs, 20 `link-api` + 20 `redirect-api` replicas, 60s, `POST /Links`)

| metric | original (all 4 problems present) | final (all fixes applied) |
|---|---|---|
| failed requests | throughput collapse | **0 / 89321 (0%)** |
| `http_req_duration` p95 | — (collapsed) | **246 ms** |
| `link-api GET /health/ready` p95 / max | **~20069 ms / ~21066 ms** | **~3060 ms / ~9811 ms** |
| `redirect-api GET /health/ready` p95 / max | **~20016 ms / ~20144 ms** | **~3022 ms / ~10365 ms** |
| pgcat `clientWaiting` | not captured (already exhausted) | 0, one transient 1-client blip, no failures caused |

`/health/ready`'s remaining ~9-15s `max` (vs. the ~3s the code now targets) is occasional scheduling
jitter under peak load, not a return of the original problem — nowhere near the original ~20-55s
figures, and Docker's own healthcheck (`interval: 5s, timeout: 5s, retries: 10`,
[`docker-compose.yml`](../docker-compose.yml)) tolerates it without flapping the container unhealthy.

**Correction (see Symptom 9 below):** the `~3060ms`/`~3022ms` p95 figures in the table above were
*not* this check settling at its intended timeout under contention — they were the check paying its
own full round-trip cost on nearly every single probe, fixed by `IRabbitMqConnection.IsOpen`.

See Symptoms 7-8 above for the matching before/after on `redirect-api.resolve` — the same class of
fix (a startup warmup instead of a per-call timeout) took `GET {hash}` from avg 7962ms/3.16% failed
to avg 70ms/0% failed on fresh containers.

## Files changed

- [`sandbox/infra/pgcat/pgcat.toml.example`](../infra/pgcat/pgcat.toml.example) — `pool_size` 10→40,
  `ban_time` 20→3 (the tracked baseline; live `pgcat.toml` is gitignored runtime state)
- [`sandbox/infra/control-api/Services/PgcatService.cs`](../infra/control-api/Services/PgcatService.cs)
  — `_pgcatPoolSettings` default 10→40, `RenderPgcatToml`'s `ban_time` 20→3 (kept in sync with
  `.example` so `GetPgcatPoolSettings()`/a fresh `pgcat.toml` don't drift back to the old values)
- [`sandbox/docker-compose.yml`](../docker-compose.yml) — `POSTGRES_MAX_CONNECTIONS` default
  100→200 (3 occurrences); `link-api`/`redirect-api` `cpus` limit 1.0→1.5 (kept as harmless headroom,
  not the actual fix — see Symptom 2)
- [`src/backend/Shared/Infrastructure/DbContextHealthCheck.cs`](../../src/backend/Shared/Infrastructure/DbContextHealthCheck.cs)
  — both health checks bounded to a 3s `CancellationTokenSource.CancelAfter` so a saturated pool fails
  the probe fast instead of hanging past Docker's own 5s timeout
- [`src/backend/Shared/Infrastructure/RabbitMqHealthCheck.cs`](../../src/backend/Shared/Infrastructure/RabbitMqHealthCheck.cs)
  — `Task.WhenAny`-based 3s race (the actual fix for the persistent ~20s figure — see Symptom 4),
  plus the losing-branch channel-disposal fix from the review pass (Symptom 5)
- [`src/backend/Shared/Infrastructure/MongoHealthCheck.cs`](../../src/backend/Shared/Infrastructure/MongoHealthCheck.cs)
  — switched to the same `Task.WhenAny` race and added a missing generic catch (Symptom 6; not
  exercised by this investigation's load, no Mongo traffic in these scenarios)
- [`src/backend/Shared/Infrastructure/RabbitMqWarmupService.cs`](../../src/backend/Shared/Infrastructure/RabbitMqWarmupService.cs)
  (new) — `IHostedService` that eagerly connects to RabbitMQ at startup (Symptom 7); registered in
  `src/backend/Services/LinkApi/Program.cs` and `src/backend/Services/RedirectApi/Program.cs`
- [`src/backend/Shared/Infrastructure/RedisWarmupService.cs`](../../src/backend/Shared/Infrastructure/RedisWarmupService.cs)
  (new) — same pattern for Redis/`IDistributedCache` and, where present, `IConnectionMultiplexer`
  (Symptom 8); registered in the same two `Program.cs` files
- [`src/backend/Shared/Infrastructure/Infrastructure.csproj`](../../src/backend/Shared/Infrastructure/Infrastructure.csproj)
  — added an explicit `Microsoft.Extensions.Caching.Abstractions` package reference for
  `RedisWarmupService`'s `IDistributedCache` (was already available transitively via `Common`, added
  directly to match this project's existing convention of listing packages a project genuinely uses)
- [`src/backend/Shared/Infrastructure/RabbitMqClient.cs`](../../src/backend/Shared/Infrastructure/RabbitMqClient.cs)
  — added `IRabbitMqConnection.IsOpen` (Symptom 9)
- [`src/backend/Shared/Infrastructure/RabbitMqHealthCheck.cs`](../../src/backend/Shared/Infrastructure/RabbitMqHealthCheck.cs)
  — checks `IsOpen` first, short-circuiting to `Healthy` without opening a channel (Symptom 9)
- [`src/backend/Shared/Infrastructure/LocalPublishQueue.cs`](../../src/backend/Shared/Infrastructure/LocalPublishQueue.cs),
  [`LocalPublishQueueWorker.cs`](../../src/backend/Shared/Infrastructure/LocalPublishQueueWorker.cs)
  (new) — bounded in-memory publish queue + draining workers, so `LinkApi`/`RedirectApi` no longer
  await `IMessagePublisher.PublishAsync` synchronously before responding (Symptom 9); registered in
  `RabbitMqExtensions.AddRabbitMqPublisher`
- [`src/backend/Shared/Infrastructure/IMessagePublisher.cs`](../../src/backend/Shared/Infrastructure/IMessagePublisher.cs),
  [`RabbitMqPublisher.cs`](../../src/backend/Shared/Infrastructure/RabbitMqPublisher.cs) — `PublishAsync`
  gained an optional `ActivityContext? parentContext` parameter so a queued publish's span still
  attaches to the original request's trace (Symptom 9)
- [`src/backend/Services/LinkApi/Controllers/LinksController.cs`](../../src/backend/Services/LinkApi/Controllers/LinksController.cs),
  [`RedirectApi/Controllers/RedirectController.cs`](../../src/backend/Services/RedirectApi/Controllers/RedirectController.cs)
  — enqueue onto `ILocalPublishQueue` instead of awaiting `IMessagePublisher.PublishAsync` directly
  (Symptom 9)

## What this meant for Pooler Scaling (built 2026-10-09)

This investigation was effectively the sizing calculation for the *single-pgcat* setup:
`pool_size × 3 database pools` had to stay under `max_connections` with real headroom for
non-pgcat connections. The Pooler Scaling feature (pgcat scaled to 3 identical replicas behind
haproxy — see `sandbox/infra/haproxy/haproxy.cfg`, `.notes/PLAN.md`'s Feature: Pooler Scaling) is
where that math actually got redone per-replica:

- `max_connections` stayed at 200 (unchanged).
- `pool_size` dropped from 40 to 20 (`sandbox/infra/pgcat/pgcat.toml.example`,
  `PgcatService._pgcatPoolSettings`'s default) — all 3 replicas mount the exact same config file
  read-only (one `pgcat` service, `deploy.replicas: 3` in docker-compose.yml - not 3 separately
  named services), so this one number governs all of them at once.
- Worst case: 20 (pool_size) × 3 (database pools) × 3 (pgcat instances) = 180 real connections to
  the primary, under 200 with ~20 left over for pgweb/exporters/admin psql/migrations — the same
  headroom reasoning Symptom 1 above used for the single-instance case, just multiplied by 3
  instances instead of by 1.
- Had `pool_size` stayed at 40 when the 3rd instance was added, the same worst case would have been
  360 — deliberately not what shipped, since demonstrating *why* a pool_size tuned for one instance
  breaks when multiplied is the whole point of this feature, not an incidental consequence.
