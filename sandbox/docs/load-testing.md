# Load testing

Load testing in this project isn't a separate script you run by hand — it's a first-class part of
the control plane. [k6](https://k6.io/) runs are configured, launched, and analyzed entirely through
the architecture-map UI (`sandbox/frontend/architecture-map`), against the real running stack, with
every infra toggle (caching, pooling, replica counts, ...) live-adjustable in between runs. This doc
covers *why* the stack is configured the way it is for real throughput, and what two real
investigations against it actually found. For the step-by-step UI walkthrough (building a step
sequence, reading the bottleneck panel, comparing runs), see
[`user-guide.md` §4.10–§4.13](user-guide.md#410-load-testing-with-k6) — this doc doesn't repeat that.

## How a run actually works

`control-api` spins up a throwaway k6 container per run (see
`sandbox/infra/control-api/Services/TrafficRunCoordinator.cs`) — there's no persistent k6 service in
`docker-compose.yml`. The container runs a generated script against whichever endpoint sequence,
ramp shape, and data-pool settings the k6 panel was configured with, streams live stats back over
SignalR, and is torn down when the run ends. This is why selecting the **k6** node in the graph
configures the *next* run rather than showing any live state of its own.

## Config that actually matters for real throughput

Three changes, found and verified during a real 10k-RPS push against the live stack (2026-09-25),
turned out to matter more than any k6/VU tuning:

- **nginx: a static `upstream {}` block with `server ... resolve;` + `keepalive 64`**, replacing a
  per-request-variable `proxy_pass` with a `resolver` directive. The old pattern re-resolved DNS and
  opened a fresh upstream connection on *every single request* — the new one keeps 64 connections to
  `link-api`/`redirect-api` open and reused, while `resolve` still means a `docker compose --scale`
  is picked up without an nginx reload (same property the old pattern existed for, just without
  paying a full connection setup per request to get it). This needs an explicit shared-memory `zone`
  directive to even start — nginx refuses at runtime with `resolving names at run time requires
  upstream ... to be in shared memory` without one, and `nginx -t` alone doesn't catch the omission;
  only starting the real container does.
- **`ServerGarbageCollection` + `ConcurrentGarbageCollection`** on `LinkApi`/`RedirectApi`. Neither
  had this set — Workstation GC (the .NET default) is tuned for desktop app responsiveness, not
  request throughput, and this project's two hottest HTTP services were running on it by accident,
  not by choice.
- **`POSTGRES_MAX_CONNECTIONS` propagated to the replicas, not just the primary.** Raising
  `max_connections` only on `postgres`'s own `command:` block looks sufficient — until a replica
  actually needs to restart, since Postgres refuses to start hot standby if a replica's
  `max_connections` is lower than the primary's. The real bug: `infra/postgres/replica-entrypoint.sh`
  ran bare `docker-entrypoint.sh postgres` with no `-c` flags at all, so a replica's effective
  `max_connections` silently came from whatever `pg_basebackup` happened to copy from the primary at
  bootstrap time — completely disconnected from anything passed to the primary's own `command:`
  afterward. Fixed by threading the same env var through to the replica's own `-c
  max_connections=...`. Both Postgres replicas and HAProxy's own `pgcat_back` pool-size math (see
  [Pooler scaling](scenarios/patterns/pooler-scaling.md)) depend on primary and replicas agreeing on
  this number — it's not just a performance knob.

None of these three needed any k6 configuration to surface — they were found by watching
`docker stats` and container logs during a real run, not by reading the compose file and guessing.

## Real bugs that only showed up under real load

Three separate bugs surfaced during the same pass, each one only visible once concurrent throughput
was high enough to expose it — each has its own before/after pitfall doc:

- 🐛 [Every RabbitMQ publish opened, declared, and closed its own channel](scenarios/pitfalls/rabbitmq-publisher-unpooled-channel-hot-path.md) —
  the actual throughput ceiling at 4 `redirect-api` replicas (2742 RPS) had nothing to do with
  PgCat or Postgres; `redirect-api` itself was pinned at 90-120% CPU per replica doing channel
  setup/teardown on every request. Fixing this alone took the same 4-replica test from 2742 RPS to
  6052 RPS, p95 205ms → 23ms.
- 🐛 [Docker-internal IPs were misclassified as public, hitting a real external API on every click](scenarios/pitfalls/traffic-geoip-dualstack-misclassified.md) —
  `traffic-service`'s click consumer fell over a million messages behind under load, acking at
  ~0.4 msg/s, because every single click triggered a real (rate-limited) `ip-api.com` call instead
  of being recognized as an internal Docker address. Fixing it alone was a ~100x improvement
  (0.4/s → ~45/s single-replica).
- 🐛 [A read-then-write click-count increment raced under multiple consumer replicas](scenarios/pitfalls/shortener-click-count-race.md) —
  scaling `shortener-service` to 4 replicas to test consumer scaling made throughput *drop*
  (190 msg/s on 1 replica → 76-79 msg/s on 4), the textbook signature of hot-row contention plus a
  real lost-update race on `Links.ClickCount`.

**The pattern worth remembering:** scaling replicas or raising a pool size doesn't help if the real
bottleneck is single-threaded work inside one request path (bug 1), an accidental external
dependency on the hot path (bug 2), or a race that gets *worse* with more replicas, not better
(bug 3). `docker stats` plus the architecture-map's own bottleneck panel (per-node CPU/memory,
queue depth, trace hop latency) is what actually found all three — not assumptions about where load
should be going.

**Also see:** [`pgcat-pool-sizing.md`](pgcat-pool-sizing.md) — a second, independent load-testing
investigation (200 VUs / 20 replicas), covering eight more real problems found the same way: pool
exhaustion, a Docker-DNS healthcheck flake, and a chain of connection-establishment cold-starts
across RabbitMQ and Redis that only mattered because a load test, not normal traffic, hit a cold
replica's first request.

## Operational gotchas (not bugs, just things that cost real time)

- `docker compose up --force-recreate --no-deps <service>` — what every standing-env toggle does
  under the hood (cache, pgcat pool, npgsql pool size, RabbitMQ prefetch, Mongo read preference) —
  resets that service back to **1 replica**, silently discarding a previous `--scale`. Re-scale
  after toggling, not before.
- A stopped `rabbitmq`/`redis`/etc. leaves queued test traffic (SQLite fallback rows, RabbitMQ
  queue depth) that looks like a real backlog in the bottleneck panel — check whether a chaos
  injection or a manual stop is still active before reading too much into a sudden queue-depth
  spike.
- Large load tests generate real RabbitMQ queue backlog as pure test noise (hundreds of thousands to
  low millions of messages at this project's test volumes) — purge test queues afterward
  (RabbitMQ's management UI, `:15672`) rather than letting `RabbitMqRetryWorker`/consumers drain
  them for hours at normal throughput.
- Confirming a real end-to-end ceiling above what's documented above needs a **distributed** k6
  setup — every number in this doc and in `pgcat-pool-sizing.md` eventually hit the single k6
  generator container's own CPU as the limiting factor, not the backend, once the backend-side bugs
  above were fixed. That's a real, separate project, not a config change to this stack.

## Where the code lives

| What | Path |
|---|---|
| Run orchestration (spins up/tears down the k6 container) | `sandbox/infra/control-api/Services/TrafficRunCoordinator.cs` |
| k6 script generation | `sandbox/infra/control-api/k6-scripts/` |
| Endpoint/data-source definitions the k6 panel picks from | `sandbox/infra/control-api/Endpoints/TrafficEndpoints.cs` |
| Bottleneck analysis | `sandbox/infra/control-api/Services/BottleneckAdvisor.cs`, tunable via `appsettings.json`'s `BottleneckThresholds` |
| nginx upstream config | `sandbox/infra/nginx/nginx.conf` |
| Postgres replica startup | `sandbox/infra/postgres/replica-entrypoint.sh` |
