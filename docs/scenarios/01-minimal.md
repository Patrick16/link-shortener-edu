# Scenario 1 — Minimal Stack

Auth, create a short link, resolve it. One Postgres instance, Redis cache, RabbitMQ already in the
loop for durability (see the note in [`docs/architecture.md`](../architecture.md#current-status) on
why the bus showed up a scenario earlier than originally planned). No sharding, no replicas, no
click tracking yet — those are later scenarios.

## What's running

| Service            | Kind    | Port  | Purpose                                                    |
|---------------------|---------|-------|--------------------------------------------------------------|
| `postgres`          | infra   | 5432  | one instance, one `linkshortener` DB, 3 schemas (see below)  |
| `redis`              | infra   | 6379  | cache for resolved links                                     |
| `rabbitmq`           | infra   | 5672 / 15672 (UI) | durable queue between LinkApi and ShortenerService |
| `auth-api`           | .NET API | 8081  | `POST /register`, `POST /login` — issues a JWT               |
| `link-api`           | .NET API | 8082  | `POST /links`, `GET /links/{hash}`                            |
| `redirect-api`       | .NET API | 8083  | `GET /{hash}` → 302 to the original URL                       |
| `shortener-service`  | worker  | —     | consumes `LinkCreatedEvent`, persists the link to Postgres    |
| `traffic-service`    | worker  | —     | running, but idle — its consumer is scenario 2 work           |
| `redisinsight`       | infra   | 5540  | Redis GUI — browse keys/TTLs, run commands (add a DB manually: host `redis`, port 6379) |
| `aspire-dashboard`   | infra   | 18888 / 18889 (OTLP) | logs, metrics, and traces from every .NET service |
| `frontend/app`       | Vite dev server | 5173 | the demo UI (create a link, log in)                     |

Each of `auth-api`/`shortener-service`/`traffic-service` **owns** one Postgres schema and is the
only one that runs `Database.MigrateAsync()` for it, applied automatically on startup:

- `auth-service.users` — owned by AuthApi
- `shortener-service.links` — owned by ShortenerService. LinkApi and RedirectApi each have their own
  `DatabaseContext` for the same table (read-only from their side) but neither migrates it — letting
  three different `DatabaseContext` types migrate the same table would race on `CREATE TABLE`, since
  each has its own independent migration history
- `traffic-service.clicks` — owned by TrafficService (exists, unused until scenario 2)

## Request flow

**Register / Login** (`AuthApi`)
1. `POST /register` (or `/login`) with `{ email, password }` (+ `name` for register)
2. Password hashed with `Microsoft.AspNetCore.Identity.PasswordHasher<T>` (PBKDF2, salt embedded in
   the hash — no separate salt column needed, even though `User.Sault` still exists on the model)
3. On success, a JWT is returned (`HS256`, claims: `sub`, `email`, `name`) — the frontend stores it
   in `localStorage` and decodes it client-side just to show the signed-in email; nothing validates
   it server-side yet (see [Known limitations](#known-limitations))

**Create a short link** (`LinkApi` → RabbitMQ → `ShortenerService`)
1. `POST /links` with `{ originalLink }`
2. `LinkApi` generates the hash itself (`Sha256Base62HashGenerator` — SHA-256 of the URL salted with
   a fresh GUID, base62-encoded, 8 chars) and returns `{ shortenLink, createdAt }` **immediately** —
   this part is synchronous
3. In the background, `LinkApi` published a `LinkCreatedEvent` to RabbitMQ. If the broker was
   unreachable, it's queued in a local SQLite fallback file instead and retried every 30s
   (`RabbitMqRetryWorker`) — the HTTP response to the caller doesn't wait on any of this
4. `ShortenerService` consumes the event and writes the row into `shortener-service.links`. This is
   the step that's genuinely asynchronous — see the timing note below

**Visit a short link** (`RedirectApi`)
1. `GET /{hash}`
2. Redis cache lookup (key `link:{hash}`, shared format with `LinkApi` — see `Shared/Common/LinkCacheService.cs`)
3. On a cache miss, reads Postgres directly, caches the result, then responds
4. `302 Found` to the original URL, or `404` if the hash doesn't exist

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
Open `http://localhost:18888` after creating a link and look at Traces: you'll see one trace named
`LinkApi: POST Links` that spans **both** `LinkApi` and `ShortenerService` — the `MSG rabbitmq
events` span on the consumer side is nested under the producer's, because the trace context is
carried across the async boundary in a `traceparent` message header (there's no mainstream
auto-instrumentation for `RabbitMQ.Client`, so this part — `Shared/Infrastructure/RabbitMqPublisher.cs`
and `RabbitMqConsumer.cs` — is hand-rolled). Worth clicking into once: it's one of the more concrete
ways to *see* what "decoupled write path" actually means end to end.

`redisinsight` (`http://localhost:5540`) gives a GUI over the same Redis cache — on first open, add
a connection with host `redis`, port `6379`, and you can watch `link:{hash}` keys appear as you
create/visit links, with their TTL counting down.

## Known limitations (by design, for now)

- **JWT isn't enforced anywhere.** `AuthApi` issues tokens; nothing validates them. `LinkApi` always
  writes `UserId = null` regardless of whether a caller is logged in. Wiring this up is tracked as
  follow-up work, not scenario 1 scope.
- **No rate limiting, no input validation beyond "is it well-formed JSON".** Fine for a learning
  project's first scenario; not something you'd want unguarded on the open internet.
- **Single Postgres instance, no replicas, no sharding.** That's scenarios 3 and 4.
- **Click tracking doesn't exist yet.** `TrafficService` is running but its consumer is a stub;
  `RedirectApi` doesn't publish anything when it resolves a link. Scenario 2.
- **`infra/rabbitmq/definitions.json` is not used.** The exchange/queue/binding topology is declared
  by the application itself (see `Shared/Infrastructure/RabbitMqPublisher.cs` and `RabbitMqConsumer.cs`),
  not loaded from a static file.

## Where the code lives

| What | Path |
|---|---|
| AuthApi | `src/Services/AuthApi/` |
| LinkApi | `src/Services/LinkApi/` |
| RedirectApi | `src/Services/RedirectApi/` |
| ShortenerService | `src/Services/ShortenerService/` |
| Shared event contracts | `src/Shared/Contracts/Events/` |
| RabbitMQ client/publisher/consumer | `src/Shared/Infrastructure/` |
| OpenTelemetry wiring (shared) | `src/Shared/ServiceDefaults/` |
| Frontend | `frontend/app/` |
| docker-compose | `docker-compose.yml` |
| Start/stop scripts | `scripts/start-stack.ps1`, `scripts/stop-stack.ps1` |
