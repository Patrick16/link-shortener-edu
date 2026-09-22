# Architecture

This project is built in scenarios, each adding one high-load pattern on top of the last (caching →
async messaging → replication → sharding → pooler scaling). This doc describes the **target**
end-state architecture; see [Current Status](#current-status) below for what's actually implemented
right now.

## Components

- **AuthApi** — registration / login, writes to `Postgres users`. Issues a JWT.
- **LinkApi** — accepts requests to create a short link, generates the hash itself and returns it
  synchronously. If the request carries a valid Bearer token, `userId` comes from its `sub` claim;
  otherwise it's `null` — no authentication is required. Publishes the created link (with hash and
  `userId?`) to RabbitMQ for `ShortenerService`.
- **ShortenerService** (worker) — listens on RabbitMQ, persists the already-hashed short link to
  `Postgres Links` (sharded, in the target design).
- **RedirectApi** — accepts a short link, resolves the origin link via `Redis Links` (cache) or
  directly from Postgres, returns the redirect, and publishes a click event to RabbitMQ for
  `TrafficService`.
- **TrafficService** (worker) — listens on RabbitMQ, writes clicks to `Postgres Clicks` and click
  metadata (parsed browser/OS/device, IP, geo) to `Mongo Clicks meta`.

## Storage

Each owning service gets its own **physical database** (not a shared database with per-service
schemas) — closer to real microservice isolation, and it's the model `pgcat` pools route on later
(scenario 4: a pool is per-database, not per-schema).

- `users_db` (owned by AuthApi) — Users(id, name, email, passwordHash, sault)
- `links_db` (owned by ShortenerService; LinkApi/RedirectApi read the same database) — target:
  **sharded** (see `infra/pgcat/shard1.toml`, `shard2.toml`): Links(hash PK, originLink, shortenLink,
  createdAt, userId?)
- `Redis Links` — cache of hash → link for fast redirects (shared key format across LinkApi/RedirectApi)
- `clicks_db` (owned by TrafficService) — Clicks(id, clickedAt, inboundLink, outboundLink, hash)
- `clicks_meta_db` (Mongo, owned by TrafficService) — clicks collection, one ClickMeta document per
  click, `_id` = the same id as the Postgres row: (hash, clickedAt, userAgent, referrer, ipAddress,
  browser?, os?, deviceType?, country?, city?)

## Infrastructure for High-Load Practice

- **Sharding** of Postgres Links via hash(key) % N through `ShardResolver` + pgcat pools per shard — scenario 4
- **Service replicas** (multiple instances of each API/worker behind nginx) — cross-cutting, not scenario-bound
- **Partitioning** of Clicks/ClicksMeta by time — scenario 2+
- **Message bus** RabbitMQ between APIs and workers (LinkApi → ShortenerService, RedirectApi → TrafficService — both live)
- **pgcat/pgbouncer** — connection pooling to each Postgres shard — scenario 4
- **nginx** — load balancer in front of the API services — cross-cutting, not built yet

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
  database-level isolation between services, not just schemas in one database. No sharding of
  `links_db` yet — that's scenario 4.
- Click *metadata* (parsed browser/OS/device via `UAParser`, IP, geo via `ip-api.com`) writes to a
  Mongo `clicks_meta_db.clicks` document alongside the Postgres `Clicks` row, same `Id`. See
  `scenarios/02-async.md#click-metadata-mongo` for what's captured and known tradeoffs.
- RabbitMQ's exchange/queue/binding topology is declared by the application itself at connection
  time (`RabbitMqPublisher`/`RabbitMqConsumer`), not loaded from `infra/rabbitmq/definitions.json` —
  that file is a placeholder for a possible future static-provisioning approach, unused right now
- Whole stack: `docker compose up -d` (backend, run from `sandbox/`) + `src/frontend/app` dev
  server — see `sandbox/scripts/start-stack.ps1` for a one-command version of both
- **Observability:** every .NET service ships logs/metrics/traces (OpenTelemetry SDK, OTLP) to a
  standalone `aspire-dashboard` container — the dashboard half of .NET Aspire, not the full AppHost
  orchestrator (docker-compose still orchestrates everything). RabbitMQ publish/consume spans are
  manually instrumented so a trace shows the full path across the async boundary for both
  `LinkApi` → `ShortenerService` and `RedirectApi` → `TrafficService`. `redisinsight` gives a GUI
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

## TODO

`infra/pgcat/`, `infra/postgres/shard1/`, `infra/postgres/shard2/` stay empty placeholders until
scenario 4 (sharding). `infra/nginx/nginx.conf` stays empty until the load-balancer cross-cutting
task is picked up.
