# Architecture

This project demonstrates a set of independent high-load patterns (caching, async messaging, read
replicas, HA/failover, partitioning, pooler scaling) you build, toggle, and observe live — not a
fixed sequence of scenarios to climb in order; see `.notes/PLAN.md`'s Terminology section. This doc
describes the **target** end-state architecture; see [Current Status](#current-status) below for
what's actually implemented right now.

**Scenario 4 changed 2026-10-06** — it used to be write-sharding (`Postgres Links` split across 2
shards via `ShardResolver` + per-shard `pgcat` pools). Replaced with Postgres native partitioning
(data retention) for `clicks_db` after an external gap-analysis pass found unbounded data growth was
a real, undemonstrated gap in this project. Write-sharding is **dropped, not deferred** — every
mention of it below is corrected or removed; see `.notes/PLAN.md`'s Mental Model section for the
full reasoning.

## Components

- **AuthApi** — registration / login, writes to `Postgres users`. Issues a JWT. Register/login/
  refresh/logout share one per-client-IP rate limit (`RateLimiting:PermitLimit`/`WindowSeconds` in
  `appsettings.json`, 100 requests/60s by default) - the sandbox overrides this to an
  effectively-unlimited quota (`AUTH_RATE_LIMIT_PERMIT`/`AUTH_RATE_LIMIT_WINDOW_SECONDS`, both in
  `sandbox/docker-compose.yml`) since every k6 VU's calls share one container's IP; lower
  `AUTH_RATE_LIMIT_PERMIT` before a run that includes Register/Login steps to see a real 429.
- **LinkApi** — accepts requests to create a short link, generates the hash itself and returns it
  synchronously. If the request carries a valid Bearer token, `userId` comes from its `sub` claim;
  otherwise it's `null` — no authentication is required. Publishes the created link (with hash and
  `userId?`) to RabbitMQ for `ShortenerService`.
- **ShortenerService** (worker) — listens on RabbitMQ, persists the already-hashed short link to
  `Postgres Links`.
- **RedirectApi** — accepts a short link, resolves the origin link via `Redis Links` (cache) or
  directly from Postgres, returns the redirect, and publishes a click event to RabbitMQ for
  `TrafficService`.
- **TrafficService** (worker) — listens on RabbitMQ, writes clicks to `Postgres Clicks` and click
  metadata (parsed browser/OS/device, IP, geo) to `Mongo Clicks meta`.

## Storage

Each owning service gets its own **physical database** (not a shared database with per-service
schemas) — closer to real microservice isolation, and it's the model `pgcat` pools route on
(one pool per database, not per-schema).

- `users_db` (owned by AuthApi) — Users(id, name, email, passwordHash, sault)
- `links_db` (owned by ShortenerService; LinkApi/RedirectApi read the same database) —
  Links(hash PK, originLink, shortenLink, createdAt, userId?)
- `Redis Links` — cache of hash → link for fast redirects (shared key format across LinkApi/RedirectApi)
- `clicks_db` (owned by TrafficService) — Clicks(id, clickedAt, inboundLink, outboundLink, hash),
  **natively partitioned** by `clickedAt` (`PARTITION BY RANGE`, monthly) — see
  `scenarios/04-partitioning.md`
- `clicks_meta_db` (Mongo, owned by TrafficService) — clicks collection, one ClickMeta document per
  click, `_id` = the same id as the Postgres row: (hash, clickedAt, userAgent, referrer, ipAddress,
  browser?, os?, deviceType?, country?, city?)

## Infrastructure for High-Load Practice

- **Service replicas** (multiple instances of each API/worker behind nginx) — cross-cutting, done
- **Partitioning** of Postgres Clicks by time (native `PARTITION BY RANGE`), plus a Mongo TTL index
  and a ClickHouse `TTL` clause for the other two stores that hold click data — done, including the
  partition-maintenance job
- **Message bus** RabbitMQ between APIs and workers (LinkApi → ShortenerService, RedirectApi → TrafficService — both live)
- **pgcat** — connection pooling to Postgres, 3 instances behind an HAProxy TCP load balancer
  (Pooler Scaling) — done
- **nginx** — load balancer in front of the API services — cross-cutting, done

---

## Current Status

**Scenario 1 is fully implemented and verified end-to-end** (backend, infra, frontend). Detailed
walkthrough, request flow, and things to try by hand: **[`scenarios/01-minimal.md`](scenarios/01-minimal.md)**.

**Scenario 2's backend and infra are also done** — click tracking works end to end (`RedirectApi` →
RabbitMQ → `TrafficService` → Postgres Clicks + Mongo ClicksMeta), verified live including the
linked trace in the dashboard. Its frontend piece isn't done yet.

Short version of what's real today:

- `AuthApi`, `LinkApi`, `RedirectApi` — all live, all tested through a real browser (not just curl)
- `LinkApi` → RabbitMQ → `ShortenerService` → Postgres and `RedirectApi` → RabbitMQ →
  `TrafficService` → Postgres are both real, working async paths, each with a SQLite fallback queue
  if RabbitMQ is briefly unreachable
- `LinkApi` optionally validates a Bearer token (no `[Authorize]` — anonymous still works) and
  stores the caller's `userId` on the link when one is present
- One Postgres server hosts three separate databases (`users_db`, `links_db`, `clicks_db`) — real
  database-level isolation between services, not just schemas in one database. `links_db` isn't
  sharded, and isn't planned to be — see the 2026-10-06 note at the top of this doc.
- Click *metadata* (parsed browser/OS/device via `UAParser`, IP, geo via `ip-api.com`) writes to a
  Mongo `clicks_meta_db.clicks` document alongside the Postgres `Clicks` row, same `Id`. See
  `scenarios/02-async.md#click-metadata-mongo` for what's captured and known tradeoffs.
- RabbitMQ's exchange/queue/binding topology is declared by the application itself at connection
  time (`RabbitMqPublisher`/`RabbitMqConsumer`), not loaded from `infra/rabbitmq/definitions.json` —
  that file is a placeholder for a possible future static-provisioning approach, unused right now
- Whole stack: `docker compose up -d` (backend, run from `sandbox/`) + `src/frontend/app` dev
  server — see `sandbox/scripts/start-stack.ps1` for a one-command version of both (it also copies
  `infra/pgcat/pgcat.toml.example` to `pgcat.toml` on first run; pgcat's config is gitignored since
  the pgcat pool-settings control rewrites it live, so copy it yourself first if running
  `docker compose up` directly on a fresh clone)
- **Observability:** every .NET service ships logs/metrics/traces (OpenTelemetry SDK, OTLP) to a
  standalone `aspire-dashboard` container — the dashboard half of .NET Aspire, not the full AppHost
  orchestrator (docker-compose still orchestrates everything). RabbitMQ publish/consume spans are
  manually instrumented so a trace shows the full path across the async boundary for both
  `LinkApi` → `ShortenerService` and `RedirectApi` → `TrafficService`. Every trace is sampled by
  default (`OTEL_TRACES_SAMPLER_ARG=1`) so demos/debugging see everything — turn it down (e.g. to
  `0.1`) before a load test, where 100% tracing adds real overhead. `redisinsight` gives a GUI
  over the Redis cache. See `scenarios/01-minimal.md#observability`.
- **Error handling:** `AuthApi`, `LinkApi`, `RedirectApi` answer every client and server error in
  the same [RFC 7807](https://www.rfc-editor.org/rfc/rfc7807) `ProblemDetails` JSON shape (`type`,
  `title`, `status`, `detail`, `traceId`) — a global `IExceptionHandler` catches anything a
  controller doesn't handle itself, and controller code that used to return a bare string
  (`Conflict("...")`, `Unauthorized("...")`) now returns `Problem(...)` instead.
- **Health checks:** all five .NET services expose `/health/live` (process is up, no dependency
  checks) and `/health/ready` (can it actually serve traffic — Postgres via `CanConnectAsync`, plus
  RabbitMQ via a real channel-open attempt for the four services that use the bus). `ShortenerService`
  and `TrafficService` carry a minimal Kestrel listener for these two routes only — they have no
  other HTTP surface. `docker-compose.yml`'s `healthcheck:` blocks poll `/health/live`; `nginx`
  waits on `link-api`/`redirect-api` being `service_healthy` before starting.
- **Read replicas / HA (read scaling) is built** — PgCat routes reads across the primary + 2
  Postgres replicas (`query_parser_read_write_splitting`), Redis runs master + 2 replicas + 3
  Sentinels with real automatic failover, Mongo runs a 3-node replica set. The conceptual write-up
  lives in [`scenarios/patterns/read-replica-routing.md`](scenarios/patterns/read-replica-routing.md)
  (no separate numbered walkthrough file — see `.notes/PLAN.md`'s Documentation note); the pitfall
  docs under `scenarios/pitfalls/` cover the real bugs found getting failover to actually work.
- **Data retention (Postgres partitioning) is done and verified live** — `clicks_db`
  is natively partitioned by `ClickedAt` (monthly), with a Mongo TTL index and a ClickHouse `TTL`
  clause covering the other two click-data stores, plus a partition-maintenance job
  (`PartitionMaintenanceWorker`) that creates future partitions ahead of time and drops ones past
  the retention window — built 2026-10-09, closing the one gap this entry used to flag. Full
  write-up: **[`scenarios/04-partitioning.md`](scenarios/04-partitioning.md)**.
- **Pooler scaling is built** — pgcat scaled to 3 identical replicas (`deploy.replicas`, same
  native mechanism link-api/redirect-api/etc. use when scaled — one `pgcat` node on the graph with
  a `×3` badge, not 3 separate nodes) behind an HAProxy TCP load balancer (`balance leastconn`,
  native `option pgsql-check`, `server-template` to track the replica count via DNS). `pool_size`
  resized from 40 to 20 to keep the aggregate connection count under Postgres's `max_connections`
  with 3 replicas instead of 1 — see `sandbox/docs/pgcat-pool-sizing.md`'s own addendum for the
  exact math.

## TODO

The dropped write-sharding plan's placeholder files (`infra/postgres/shard1/`,
`infra/postgres/shard2/`, `infra/pgcat/shard1.toml`, `infra/pgcat/shard2.toml`,
`docker-compose.shard1.yml`, `docker-compose.shard2.yml`, `Shared/Infrastructure/ShardResolver.cs`)
were removed 2026-10-06 alongside this doc update — all were empty `TODO`-only stubs with no real
code or references anywhere else.
