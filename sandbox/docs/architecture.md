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
- **TrafficService** (worker) — listens on RabbitMQ, writes clicks to `Postgres Clicks`. Target
  design also has it writing metadata (user-agent, referrer, headers) to `Mongo Clicks meta` — that
  part isn't built (Mongo isn't in the stack yet).

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
- `Mongo Clicks meta` — target: ClicksMeta(id, clickedAt, userAgent, referrer, origin, headers) — not built yet

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
walkthrough, request flow, and things to try by hand: **[`docs/scenarios/01-minimal.md`](scenarios/01-minimal.md)**.

**Scenario 2's backend and infra are also done** — click tracking works end to end (`RedirectApi` →
RabbitMQ → `TrafficService` → Postgres Clicks), verified live including the linked trace in the
dashboard. Its frontend piece and write-up doc aren't done yet.

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
- Click *metadata* (user-agent, referrer, headers → Mongo) is still not built — Mongo isn't in the
  stack. Only the Postgres `Clicks` row (hash, in/outbound link, timestamp) exists.
- RabbitMQ's exchange/queue/binding topology is declared by the application itself at connection
  time (`RabbitMqPublisher`/`RabbitMqConsumer`), not loaded from `infra/rabbitmq/definitions.json` —
  that file is a placeholder for a possible future static-provisioning approach, unused right now
- Whole stack: `docker compose up -d` (backend) + `frontend/app` dev server — see
  `scripts/start-stack.ps1` for a one-command version of both
- **Observability:** every .NET service ships logs/metrics/traces (OpenTelemetry SDK, OTLP) to a
  standalone `aspire-dashboard` container — the dashboard half of .NET Aspire, not the full AppHost
  orchestrator (docker-compose still orchestrates everything). RabbitMQ publish/consume spans are
  manually instrumented so a trace shows the full path across the async boundary for both
  `LinkApi` → `ShortenerService` and `RedirectApi` → `TrafficService`. `redisinsight` gives a GUI
  over the Redis cache. See `docs/scenarios/01-minimal.md#observability`.

## TODO

`infra/pgcat/`, `infra/postgres/shard1/`, `infra/postgres/shard2/` stay empty placeholders until
scenario 4 (sharding). `infra/nginx/nginx.conf` stays empty until the load-balancer cross-cutting
task is picked up.
