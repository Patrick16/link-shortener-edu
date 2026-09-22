# Scenario 2 — Click Tracking (Async)

> **Status:** backend and infra are done and verified live, including the Mongo click-metadata half
> added after this scenario was first written (see below); the frontend piece (a dashboard showing
> a user's links/click counts) and this scenario's UI story aren't built yet. This doc covers what
> exists: click tracking through the bus.

Scenario 1 already had `LinkApi → RabbitMQ → ShortenerService` (the bus arrived a scenario early —
see `docs/architecture.md`). What scenario 2 actually adds is the second async path:
`RedirectApi → RabbitMQ → TrafficService`, so a click is recorded without slowing down or risking
the redirect itself.

## What's new since scenario 1

- `clicks_db.clicks` now actually gets rows written to it (the database and table existed since
  scenario 1 — migrated, but nothing ever consumed into it)
- `RedirectApi` publishes to RabbitMQ, same resilience pattern as `LinkApi`: SQLite fallback file +
  a background retry worker if the broker is briefly unreachable
- `RabbitMqRetryWorker` moved from `LinkApi` to `Shared/Infrastructure` — it was never actually
  LinkApi-specific, and `RedirectApi` needed the exact same thing

## Request flow

`GET /{hash}` on `RedirectApi`, after resolving the link (cache hit or Postgres fallback) and
**before** issuing the 302:

1. Builds a `ClickTrackedEvent`: `Id` (fresh GUID, generated here — this is what makes the consumer
   redelivery-safe), `Hash`, `InboundLink` (the short URL that was visited, via
   `HttpContext.Request.GetDisplayUrl()`), `OutboundLink` (the resolved destination), `ClickedAt`,
   plus the raw `UserAgent`/`Referer` headers and the caller's `IpAddress` — captured here, off the
   HTTP request, but not parsed or geo-resolved until `TrafficService` picks the event up
2. Publishes it with routing key `click.tracked` on the same `events` topic exchange `link.created`
   uses — same exchange, different routing key, so `TrafficService`'s queue binding only receives
   this one
3. Returns the 302

A `404` (unknown hash) never publishes anything — there's no click to record.

`TrafficService`'s `ClickTrackedConsumer` (`src/backend/Services/TrafficService/ClickTrackedConsumer.cs`):

1. Consumes from queue `traffic-service.click-tracked`
2. Checks whether a `Click` with the event's `Id` already exists — if so, this is a redelivery
   (e.g. after a nack), skip it rather than fail on the primary-key violation. In that case Mongo
   isn't touched either — both writes are skipped together.
3. Otherwise inserts the row into `clicks_db.clicks`
4. Parses the raw `UserAgent` into browser/OS/device type (`UAParser`, fully offline — no external
   call), resolves country/city from `IpAddress` via `ip-api.com` (best-effort: private/loopback
   addresses — the common case for a local docker-compose click — and any lookup failure just leave
   geo fields `null`, they never fail the click), and upserts a `ClickMeta` document into Mongo
   (`clicks_meta_db.clicks`), keyed by the same `Id` as the Postgres row
5. Acks

## Click metadata (Mongo)

Added after this scenario's initial pass. `clicks_meta_db` is a separate MongoDB instance from
Postgres — schema-less by design, since the fields here (parsed UA, geo) are exactly the kind of
data that grows independently of the core click record without needing a migration. The two writes
(Postgres `Click`, Mongo `ClickMeta`) are **not transactional with each other** — each is
independently redelivery-safe (Postgres via the existing `Id`-exists check; Mongo via an upsert by
`Id`), but a crash between the two writes could in principle leave one without the other. Not
addressed further — same class of tradeoff as the rest of this dual-write-without-a-saga codebase.

```bash
docker compose exec mongo mongosh --quiet clicks_meta_db \
  --eval "printjson(db.clicks.find().sort({ClickedAt:-1}).limit(5).toArray())"
```

## Try it yourself

```bash
# Assumes a link already exists — grab a hash from scenario 1's example, then:
curl -i http://localhost:8083/<hash>          # triggers the click event
# give the consumer a moment, then check RabbitMQ's management UI (http://localhost:15672) —
# queue "traffic-service.click-tracked" briefly shows 1 message, then drains to 0
```

Or check the row directly:

```bash
docker exec -it $(docker compose ps -q postgres) psql -U postgres -d clicks_db \
  -c 'SELECT * FROM clicks ORDER BY "ClickedAt" DESC LIMIT 5;'
```

Or open the Aspire Dashboard (`http://localhost:18888/traces`) right after visiting a link — look
for `RedirectApi: GET {hash}`; it spans into `TrafficService` the same way `LinkApi: POST Links`
spans into `ShortenerService` (see `docs/scenarios/01-minimal.md#observability`).

## What's still missing from the target design

- **Dead-letter handling** — a message that fails processing every time just requeues forever.
  No DLQ, no poison-message limit.
- **Frontend** — no UI shows click counts yet (`DashboardPage` is still a stub).
- **Geo accuracy in local dev** — the client IP `RedirectApi` sees is whatever's on the connection
  (docker-internal address behind `nginx`, not a real `X-Forwarded-For` chain), so it's almost
  always a private-range address and geo comes back `null`. Expected in this stack; not something
  to "fix" without an actual reverse-proxy chain forwarding a real client IP.

## Where the code lives

| What | Path |
|---|---|
| `ClickTrackedEvent` | `src/backend/Shared/Contracts/Events/ClickTrackedEvent.cs` |
| `Topics.ClickTracked` | `src/backend/Shared/Contracts/Events/Topics.cs` |
| RedirectApi publish | `src/backend/Services/RedirectApi/Controllers/RedirectController.cs` |
| TrafficService consumer | `src/backend/Services/TrafficService/ClickTrackedConsumer.cs` |
| Shared retry worker | `src/backend/Shared/Infrastructure/RabbitMqRetryWorker.cs` |
| `ClickMeta` document | `src/backend/Shared/Common/Models/ClickMeta.cs` |
| Mongo store | `src/backend/Shared/Infrastructure/MongoClickMetaStore.cs` |
| UA parser | `src/backend/Shared/Common/UaParserUserAgentParser.cs` |
| Geo IP resolver | `src/backend/Shared/Infrastructure/IpApiGeoIpResolver.cs` |
