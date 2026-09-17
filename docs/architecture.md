# Architecture

This project is built in scenarios, each adding one high-load pattern on top of the last (caching →
async messaging → replication → sharding → pooler scaling). This doc describes the **target**
end-state architecture; see [Current Status](#current-status) below for what's actually implemented
right now.

## Components

- **AuthApi** — registration / login, writes to `Postgres users`. Issues a JWT.
- **LinkApi** — accepts requests to create a short link, generates the hash itself and returns it
  synchronously. Publishes the created link (with hash) to RabbitMQ for `ShortenerService`. Once
  auth is wired through end-to-end, an authenticated request will carry `userId` into the event.
- **ShortenerService** (worker) — listens on RabbitMQ, persists the already-hashed short link to
  `Postgres Links` (sharded, in the target design).
- **RedirectApi** — accepts a short link, resolves the origin link via `Redis Links` (cache) or
  directly from Postgres, returns the redirect. Target design: also publishes a click event to
  RabbitMQ for `TrafficService`.
- **TrafficService** (worker) — target design: listens on RabbitMQ, writes clicks to
  `Postgres Clicks` and metadata (user-agent, referrer, headers) to `Mongo Clicks meta`.

## Storage

- `Postgres users` (schema `auth-service`) — Users(id, name, email, passwordHash, sault)
- `Postgres Links` (schema `shortener-service`) — target: **sharded** (see `infra/pgcat/shard1.toml`,
  `shard2.toml`): Links(hash PK, originLink, shortenLink, createdAt, userId?)
- `Redis Links` — cache of hash → link for fast redirects (shared key format across LinkApi/RedirectApi)
- `Postgres Clicks` (schema `traffic-service`) — target: Clicks(id, clickedAt, inboundLink, outboundLink, hash)
- `Mongo Clicks meta` — target: ClicksMeta(id, clickedAt, userAgent, referrer, origin, headers) — not built yet

## Infrastructure for High-Load Practice

- **Sharding** of Postgres Links via hash(key) % N through `ShardResolver` + pgcat pools per shard — scenario 4
- **Service replicas** (multiple instances of each API/worker behind nginx) — cross-cutting, not scenario-bound
- **Partitioning** of Clicks/ClicksMeta by time — scenario 2+
- **Message bus** RabbitMQ between APIs and workers (LinkApi → ShortenerService now; RedirectApi → TrafficService planned)
- **pgcat/pgbouncer** — connection pooling to each Postgres shard — scenario 4
- **nginx** — load balancer in front of the API services — cross-cutting, not built yet

---

## Current Status

**Scenario 1 is fully implemented and verified end-to-end** (backend, infra, frontend). Detailed
walkthrough, request flow, and things to try by hand: **[`docs/scenarios/01-minimal.md`](scenarios/01-minimal.md)**.

Short version of what's real today:

- `AuthApi`, `LinkApi`, `RedirectApi` — all live, all tested through a real browser (not just curl)
- `LinkApi` → RabbitMQ → `ShortenerService` → Postgres is a real, working async write path, with a
  SQLite fallback queue if RabbitMQ is briefly unreachable
- One Postgres instance holds all three schemas (no sharding yet — that's scenario 4)
- `TrafficService`/click-tracking/Mongo are **not built** — that's scenario 2
- RabbitMQ's exchange/queue/binding topology is declared by the application itself at connection
  time (`RabbitMqPublisher`/`RabbitMqConsumer`), not loaded from `infra/rabbitmq/definitions.json` —
  that file is a placeholder for a possible future static-provisioning approach, unused right now
- Whole stack: `docker compose up -d` (backend) + `frontend/app` dev server — see
  `scripts/start-stack.ps1` for a one-command version of both

## TODO

`infra/pgcat/`, `infra/postgres/shard1/`, `infra/postgres/shard2/` stay empty placeholders until
scenario 4 (sharding). `infra/nginx/nginx.conf` stays empty until the load-balancer cross-cutting
task is picked up.
