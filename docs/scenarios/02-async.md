# Scenario 2 — Click Tracking (Async)

> **Status:** backend and infra are done and verified live; the frontend piece (a dashboard showing
> a user's links/click counts) and this scenario's UI story aren't built yet. This doc covers what
> exists: click tracking through the bus.

Scenario 1 already had `LinkApi → RabbitMQ → ShortenerService` (the bus arrived a scenario early —
see `docs/architecture.md`). What scenario 2 actually adds is the second async path:
`RedirectApi → RabbitMQ → TrafficService`, so a click is recorded without slowing down or risking
the redirect itself.

## What's new since scenario 1

- `traffic-service.clicks` now actually gets rows written to it (it existed since scenario 1 —
  migrated, but nothing ever consumed into it)
- `RedirectApi` publishes to RabbitMQ, same resilience pattern as `LinkApi`: SQLite fallback file +
  a background retry worker if the broker is briefly unreachable
- `RabbitMqRetryWorker` moved from `LinkApi` to `Shared/Infrastructure` — it was never actually
  LinkApi-specific, and `RedirectApi` needed the exact same thing

## Request flow

`GET /{hash}` on `RedirectApi`, after resolving the link (cache hit or Postgres fallback) and
**before** issuing the 302:

1. Builds a `ClickTrackedEvent`: `Id` (fresh GUID, generated here — this is what makes the consumer
   redelivery-safe), `Hash`, `InboundLink` (the short URL that was visited, via
   `HttpContext.Request.GetDisplayUrl()`), `OutboundLink` (the resolved destination), `ClickedAt`
2. Publishes it with routing key `click.tracked` on the same `events` topic exchange `link.created`
   uses — same exchange, different routing key, so `TrafficService`'s queue binding only receives
   this one
3. Returns the 302

A `404` (unknown hash) never publishes anything — there's no click to record.

`TrafficService`'s `ClickTrackedConsumer` (`src/Services/TrafficService/ClickTrackedConsumer.cs`):

1. Consumes from queue `traffic-service.click-tracked`
2. Checks whether a `Click` with the event's `Id` already exists — if so, this is a redelivery
   (e.g. after a nack), skip it rather than fail on the primary-key violation
3. Otherwise inserts the row into `traffic-service.clicks` and acks

## Try it yourself

```bash
# Assumes a link already exists — grab a hash from scenario 1's example, then:
curl -i http://localhost:8083/<hash>          # triggers the click event
# give the consumer a moment, then check RabbitMQ's management UI (http://localhost:15672) —
# queue "traffic-service.click-tracked" briefly shows 1 message, then drains to 0
```

Or check the row directly:

```bash
docker exec -it $(docker compose ps -q postgres) psql -U postgres -d linkshortener \
  -c 'SELECT * FROM "traffic-service".clicks ORDER BY "ClickedAt" DESC LIMIT 5;'
```

Or open the Aspire Dashboard (`http://localhost:18888/traces`) right after visiting a link — look
for `RedirectApi: GET {hash}`; it spans into `TrafficService` the same way `LinkApi: POST Links`
spans into `ShortenerService` (see `docs/scenarios/01-minimal.md#observability`).

## What's still missing from the target design

- **Click metadata** (user-agent, referrer, request headers → Mongo `ClicksMeta`) — Mongo isn't in
  `docker-compose.yml` yet, so this half of the original design isn't built. `ClickTrackedEvent`
  doesn't currently carry these fields either; they'd need adding if/when Mongo shows up.
- **Dead-letter handling** — a message that fails processing every time just requeues forever.
  No DLQ, no poison-message limit.
- **Frontend** — no UI shows click counts yet (`DashboardPage` is still a stub).

## Where the code lives

| What | Path |
|---|---|
| `ClickTrackedEvent` | `src/Shared/Contracts/Events/ClickTrackedEvent.cs` |
| `Topics.ClickTracked` | `src/Shared/Contracts/Events/Topics.cs` |
| RedirectApi publish | `src/Services/RedirectApi/Controllers/RedirectController.cs` |
| TrafficService consumer | `src/Services/TrafficService/ClickTrackedConsumer.cs` |
| Shared retry worker | `src/Shared/Infrastructure/RabbitMqRetryWorker.cs` |
