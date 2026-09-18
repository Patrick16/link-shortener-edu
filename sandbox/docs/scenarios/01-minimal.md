# Scenario 1 — Minimal Stack

Auth, create a short link, resolve it. One Postgres server hosting three separate databases, Redis
cache, RabbitMQ already in the loop for durability (see the note in
[`docs/architecture.md`](../architecture.md#current-status) on why the bus showed up a scenario
earlier than originally planned). No sharding, no replicas yet — those are later scenarios. (Click
tracking — scenario 2 — is also live; see [`docs/scenarios/02-async.md`](02-async.md).)

## What's running

| Service            | Kind    | Port  | Purpose                                                    |
|---------------------|---------|-------|--------------------------------------------------------------|
| `postgres`          | infra   | 5432  | one server, three databases (see below): `users_db`, `links_db`, `clicks_db` |
| `redis`              | infra   | 6379  | cache for resolved links                                     |
| `rabbitmq`           | infra   | 5672 / 15672 (UI) | durable queue between the APIs and their workers  |
| `auth-api`           | .NET API | 8081  | `POST /register`, `POST /login` — issues a JWT               |
| `link-api`           | .NET API | 8082  | `POST /links`, `GET /links/{hash}`                            |
| `redirect-api`       | .NET API | 8083  | `GET /{hash}` → 302 to the original URL                       |
| `shortener-service`  | worker  | —     | consumes `LinkCreatedEvent`, persists the link to Postgres    |
| `traffic-service`    | worker  | —     | consumes `ClickTrackedEvent`, persists the click to Postgres  |
| `redisinsight`       | infra   | 5540  | Redis GUI — browse keys/TTLs, run commands (add a DB manually: host `redis`, port 6379) |
| `aspire-dashboard`   | infra   | 18888 / 18889 (OTLP) | logs, metrics, and traces from every .NET service |
| `src/frontend/app`   | Vite dev server | 5173 | the demo UI (create a link, log in)                     |

Each of `auth-api`/`shortener-service`/`traffic-service` **owns** one physical database — a
deliberate choice over schemas-in-one-database, closer to real microservice isolation and matching
how `pgcat` pools get configured later (scenario 4: one pool per database, not per schema). Each
owner is the only service that runs `Database.MigrateAsync()` for its database, applied
automatically on startup (see `infra/postgres/init-databases.sql`, relative to `sandbox/`, for how
the three databases get
created in the first place):

- `users_db` — owned by AuthApi
- `links_db` — owned by ShortenerService. LinkApi and RedirectApi each have their own
  `DatabaseContext` pointed at the same database (read-only from their side) but neither migrates
  it — letting three different `DatabaseContext` types migrate the same table would race on
  `CREATE TABLE`, since each has its own independent migration history
- `clicks_db` — owned by TrafficService

## Request flow

**Register / Login** (`AuthApi`)
1. `POST /register` (or `/login`) with `{ email, password }` (+ `name` for register)
2. Password hashed with `Microsoft.AspNetCore.Identity.PasswordHasher<T>` (PBKDF2, salt embedded in
   the hash — no separate salt column needed, even though `User.Sault` still exists on the model)
3. On success, a JWT is returned (`HS256`, claims: `sub`, `email`, `name`) — the frontend stores it
   in `localStorage` and decodes it client-side just to show the signed-in email. `LinkApi` also
   validates this same token (see the next section) — same signing key, checked in `Common.Constants`.

**Create a short link** (`LinkApi` → RabbitMQ → `ShortenerService`)
1. `POST /links` with `{ originalLink }`, optionally with `Authorization: Bearer <token>`
2. `LinkApi` generates the hash itself (`Sha256Base62HashGenerator` — SHA-256 of the URL salted with
   a fresh GUID, base62-encoded, 8 chars) and returns `{ shortenLink, createdAt }` **immediately** —
   this part is synchronous. If the request carried a valid token, its `sub` claim becomes the
   link's `userId`; no token (or an invalid/expired one) just means `userId` stays `null` — the
   request never gets rejected for it, there's no `[Authorize]` on this endpoint
3. In the background, `LinkApi` published a `LinkCreatedEvent` (including `userId?`) to RabbitMQ.
   If the broker was unreachable, it's queued in a local SQLite fallback file instead and retried
   every 30s (`RabbitMqRetryWorker`, shared with `RedirectApi` — see `Shared/Infrastructure/`) — the
   HTTP response to the caller doesn't wait on any of this
4. `ShortenerService` consumes the event and writes the row into `links_db.links`. This is
   the step that's genuinely asynchronous — see the timing note below

**Visit a short link** (`RedirectApi` → RabbitMQ → `TrafficService`)
1. `GET /{hash}`
2. Redis cache lookup (key `link:{hash}`, shared format with `LinkApi` — see `Shared/Common/LinkCacheService.cs`)
3. On a cache miss, reads Postgres directly, caches the result
4. Publishes a `ClickTrackedEvent` (hash, the short URL visited, the resolved destination,
   timestamp) — same publish-or-fall-back-to-SQLite pattern as link creation
5. `302 Found` to the original URL, or `404` if the hash doesn't exist (a 404 never gets a click event)
6. `TrafficService` consumes the event and writes the row into `clicks_db.clicks`

## Try it yourself

### Fastest: the start script

```powershell
.\scripts\start-stack.ps1
```

Backend comes up via `docker compose`, the frontend dev server opens in its own window, and a
browser tab opens to `http://localhost:5173`. `.\scripts\stop-stack.ps1` shuts the backend down
(`-Wipe` also drops the Postgres volume, for a clean-slate restart).

### By hand, with curl

```bash
# Register (also logs you in — the response carries a token)
curl -X POST http://localhost:8081/register \
  -H "Content-Type: application/json" \
  -d '{"name":"Ada","email":"ada@example.com","password":"correct-horse-battery"}'

# Create a link
curl -X POST http://localhost:8082/links \
  -H "Content-Type: application/json" \
  -d '{"originalLink":"https://example.com"}'
# => {"shortenLink":"AbC12xYz","createdAt":"..."}

# Follow it (302 to https://example.com)
curl -i http://localhost:8083/AbC12xYz
```

RabbitMQ's management UI (`http://localhost:15672`, guest/guest) is worth a look after creating a
link — you'll see the `link.created` routing key hit the `shortener-service.link-created` queue.

## A thing worth noticing

Step 3 above (`LinkApi` returns) and step 4 (`ShortenerService` persists) are decoupled. In the
normal case the gap is milliseconds, but if you `GET /links/{hash}` or visit `GET /{hash}`
*immediately* after creating a link, you can occasionally get a `404` before the consumer catches
up. The demo doesn't hide this — the frontend shows a small note about it after creating a link.
This is the actual point of scenario 1 having a bus already: it's a small, honest example of
eventual consistency, which gets more interesting once replicas and sharding are in the picture.

## Observability

Every .NET service exports logs, metrics, and traces (OpenTelemetry SDK, OTLP over gRPC) to the
`aspire-dashboard` container — the lightweight half of the .NET Aspire pattern (a standalone
dashboard, not the full AppHost orchestrator; docker-compose stays in charge of orchestration).
Open `http://localhost:18888` after creating a link (or visiting one) and look at Traces: you'll
see traces named `LinkApi: POST Links` and `RedirectApi: GET {hash}`, each spanning **both** the
API and the worker that ends up doing the write (`ShortenerService`, `TrafficService`) — the `MSG
rabbitmq events` span on the consumer side is nested under the producer's, because the trace
context is carried across the async boundary in a `traceparent` message header (there's no
mainstream auto-instrumentation for `RabbitMQ.Client`, so this part —
`Shared/Infrastructure/RabbitMqPublisher.cs` and `RabbitMqConsumer.cs` — is hand-rolled). Worth
clicking into once: it's one of the more concrete ways to *see* what "decoupled write path"
actually means end to end.

`redisinsight` (`http://localhost:5540`) gives a GUI over the same Redis cache — on first open, add
a connection with host `redis`, port `6379`, and you can watch `link:{hash}` keys appear as you
create/visit links, with their TTL counting down.

## Known limitations (by design, for now)

- **No rate limiting, no input validation beyond "is it well-formed JSON".** Fine for a learning
  project's first scenario; not something you'd want unguarded on the open internet.
- **Single Postgres instance, no replicas, no sharding.** That's scenarios 3 and 4.
- **Click *metadata* doesn't exist yet.** The `Clicks` row itself (hash, in/outbound link, timestamp)
  is real — but user-agent/referrer/headers → Mongo `ClicksMeta` isn't built (Mongo isn't in the
  stack). See `../architecture.md`.
- **No dead-letter queue.** Both consumers (`ShortenerService`, `TrafficService`) nack-and-requeue
  forever on a persistent processing failure — a poison message would loop indefinitely rather than
  landing somewhere for inspection.
- **`infra/rabbitmq/definitions.json` (relative to `sandbox/`) is not used.** The exchange/queue/
  binding topology is declared by the application itself (see
  `src/backend/Shared/Infrastructure/RabbitMqPublisher.cs` and `RabbitMqConsumer.cs`), not loaded
  from a static file.

## Where the code lives

| What | Path |
|---|---|
| AuthApi | `src/backend/Services/AuthApi/` |
| LinkApi (incl. JWT validation) | `src/backend/Services/LinkApi/` |
| RedirectApi | `src/backend/Services/RedirectApi/` |
| ShortenerService | `src/backend/Services/ShortenerService/` |
| TrafficService | `src/backend/Services/TrafficService/` |
| Shared event contracts | `src/backend/Shared/Contracts/Events/` |
| RabbitMQ client/publisher/consumer | `src/backend/Shared/Infrastructure/` |
| OpenTelemetry wiring (shared) | `src/backend/Shared/ServiceDefaults/` |
| Frontend | `src/frontend/app/` |
| docker-compose | `sandbox/docker-compose.yml` |
| Start/stop scripts | `sandbox/scripts/start-stack.ps1`, `sandbox/scripts/stop-stack.ps1` |
