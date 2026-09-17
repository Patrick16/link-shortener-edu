# Architecture

## Components

- **AuthApi** — registration / login, writes to `Postgres users`. Provides `UserAuth` to other services.
- **LinkApi** — accepts requests to create a short link, generates the hash itself and returns it synchronously. If the user is authenticated, `userId` is written into the event. Publishes the created link (with hash) to RabbitMQ for `ShortenerService`.
- **ShortenerService** (worker) — listens on RabbitMQ, persists the already-hashed short link to `Postgres Links` (sharded).
- **RedirectApi** — accepts a short link, resolves the origin link via `Redis Links` (cache) or directly, returns the redirect, publishes a click event to RabbitMQ.
- **TrafficService** (worker) — listens on RabbitMQ, writes clicks to `Postgres Clicks Users` and metadata (user-agent, referrer, headers) to `Mongo Clicks meta`.

## Storage

- `Postgres users` — Users(id, name, email, passwordHash, sault)
- `Postgres Links` — **sharded** (see `infra/pgcat/shard1.toml`, `shard2.toml`): Links(hash PK, originLink, shortenLink, userId?)
- `Redis Links` — cache of hash → originLink for fast redirects
- `Postgres Clicks Users` — Clicks(id, clickedAt, inboundLink, outboundLink, hash)
- `Mongo Clicks meta` — ClicksMeta(id, clickedAt, userAgent, referrer, origin, headers)

## Infrastructure for High-Load Practice

- **Sharding** of Postgres Links via hash(key) % N through `ShardResolver` + pgcat pools per shard
- **Service replicas** (multiple instances of each API/worker behind nginx)
- **Partitioning** of Clicks/ClicksMeta by time
- **Message bus** RabbitMQ between APIs and workers (LinkApi → ShortenerService, RedirectApi → TrafficService)
- **pgcat/pgbouncer** — connection pooling to each Postgres shard
- **nginx** — load balancer in front of the API services

## TODO

Details (schema, pgcat/nginx/rabbitmq configs, docker-compose) are filled in as the project progresses — see `infra/` and the root `docker-compose*.yml` files.
