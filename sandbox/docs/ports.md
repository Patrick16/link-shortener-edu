# Port map

Every host port this stack can publish, by service. Source of truth is always
[`docker-compose.yml`](../docker-compose.yml) — this file exists so you don't have to grep it to
find a port, not to duplicate it as a second thing to keep in sync; if the two disagree, the
compose file is right.

## Product (the thing being demonstrated)

| Port | Service | Notes |
|---|---|---|
| 8081 | `auth-api` | direct, not behind nginx (login/register is low-traffic, no need to load-balance) |
| 8082 | `nginx` → `link-api` | round-robins across however many `link-api` replicas exist |
| 8083 | `nginx` → `redirect-api` | same, for `redirect-api` replicas |
| 8088 | `reporting-api` | direct, same reasoning as `auth-api` (read-only, low-traffic) |
| 5173 | `src/frontend/app` dev server | not compose-managed — started by `start-stack` scripts, floats if taken |

`link-api`/`redirect-api` themselves publish no ports — only reachable through `nginx` above (or
directly inside the Docker network, which is what the **nginx bypass** toggle switches traffic
generation to).

## Data stores

| Port | Service | Notes |
|---|---|---|
| 5432 | `postgres` (primary) | |
| 5433 | `postgres-replica1` | confirm standby via `SELECT pg_is_in_recovery()` |
| 5434 | `postgres-replica2` | |
| 6379 | `redis-master` | |
| 26379 | `redis-sentinel-1` | |
| 26380 | `redis-sentinel-2` | container's own port is 26379, remapped on the host to avoid clashing with sentinel-1 |
| 26381 | `redis-sentinel-3` | same remap pattern |
| 5672 | `rabbitmq` | AMQP |
| 15672 | `rabbitmq` management UI | gated by `RABBITMQ_UI_PORT` env var (default 15672, empty = not published) — see note below |
| 15692 | `rabbitmq` Prometheus metrics | `rabbitmq_prometheus` plugin, scraped by the `observability` profile |
| 27017 | `mongo1` | |
| 27018 | `mongo2` | container's own port is 27017, remapped same as the sentinels |
| 27019 | `mongo3` | |
| 8123 | `clickhouse` | HTTP interface |

`redis-replica1`/`redis-replica2` publish no ports (only reachable inside the Docker network).

## Pooling layer

| Port | Service | Notes |
|---|---|---|
| 6432 | `haproxy` → `pgcat` (×3 replicas) | the pooler's single entry point — every DB-touching service connects here, never to a pgcat instance directly |
| 8405 | `haproxy` stats page | also scraped by Prometheus, see `infra/haproxy/haproxy.cfg` |

`pgcat` itself publishes no ports since the Pooler Scaling feature moved it behind `haproxy`
(2026-10-09) — `haproxy`'s 6432 above is the only way in from the host.

## Control plane

| Port | Service | Notes |
|---|---|---|
| 5299 | `control-api` | bound to `127.0.0.1` only — holds the host's Docker socket with no auth on top, see its compose comment |
| 5174 | `sandbox/frontend/architecture-map` dev server | `strictPort` — always exactly 5174, not compose-managed |

## Observability

| Port | Service | Notes |
|---|---|---|
| 18888 | `aspire-dashboard` UI | `Aspire`/`None` variants only |
| 18889 | `aspire-dashboard` OTLP gRPC | otel-collector forwards logs/metrics/traces here |
| 8889 | `otel-collector` Prometheus scrape | `otel` profile; harmless if published outside the `Full` variant, just unused |
| 9090 | `prometheus` | `observability` profile (`-Observability Full`) |
| 16686 | `jaeger` UI | `observability` profile |
| 3100 | `loki` | `observability` profile |
| 3000 | `grafana` | `observability` profile |

## Optional browser UIs

Each gated by its own `ui-*` compose profile (`--skip-*-ui` flags on the start scripts skip
starting them, not just hide them):

| Port | Service | Profile |
|---|---|---|
| 8084 | `pgweb` | `ui-postgres` |
| 5540 | `redisinsight` | `ui-redis` |
| 8085 | `mongo-express` | `ui-mongo` |
| 8086 | `link-api-fallback-viewer` (sqlite-web) | `ui-sqlite` |
| 8087 | `redirect-api-fallback-viewer` (sqlite-web) | `ui-sqlite` |

## The `RABBITMQ_UI_PORT` exception

Every other optional UI above is gated by a compose profile — the container itself doesn't start
unless the profile is active. RabbitMQ's management UI can't work that way: it's the *same*
container as the broker, not a separate one, so the broker would have to skip starting too. It's
instead gated by whether port 15672 is published at all (`RABBITMQ_UI_PORT` env var, empty =
nothing published) — the broker itself always runs regardless.
