## What it is

An ASP.NET Core Web API with two unrelated jobs bundled into one service: a read-only query
endpoint over ClickHouse (the CQRS read side — see [CQRS fan-out](pattern:cqrs-fanout)), and a
SignalR hub that pushes each user's live top-10-links list over a WebSocket. Every endpoint and the
hub both require a valid JWT — unlike LinkApi/RedirectApi's optional-token posture, there's no
anonymous path through this service at all, since both features expose per-account data.

## What it solves

**Reports.** `reports_db` in ClickHouse is a second, independent copy of click data, written by
[ReportingService](node:reporting-service) specifically so aggregate queries (by day/country/
device/browser) run against a columnar store built for that, instead of competing with
[TrafficService](node:traffic-service)'s own Postgres/Mongo writes on the same data. ReportingApi
is the only thing that reads it.

**Live top links.** A registered user wants to see their own top-10 links by clicks over the last 7
days, updating as clicks happen — without polling. [RedirectApi](node:redirect-api) writes every
owned click into Redis; ReportingApi reads the rolling window back and pushes it to any open
dashboard the instant a watched user's list actually changes. A *global* top list would leak other
users' link stats, so this is deliberately per-user only.

## How it works

**Reports:** `GET /reports/{hash}/summary` validates the hash format, then runs one raw
parameterized SQL query against ClickHouse and returns the aggregate. No ORM here on purpose — this
is the one place in the project meant to show what a real columnar OLAP query looks like, not
hidden behind a query builder.

**Owner-scoped reports.** The endpoint only ever returns data for hashes the caller owns.
`ClickHouseClickFactQueryService.GetSummaryAsync` runs the 4 per-owner bucket queries (now filtered
`AND user_id = callerId`) alongside a 5th scalar query — `count() WHERE hash = ? AND user_id IS NOT
NULL AND user_id != callerId` — concurrently via `Task.WhenAll`; that 5th query is the only thing
distinguishing "owned, zero clicks yet" (ordinary all-zero `200`) from "owned by someone else"
(`null` from the query service, which `ReportsController` turns into a plain `404`). The owner
travels with the click itself (`RedirectController` already holds `link.UserId` when it publishes
`ClickTrackedEvent`; `ClickTrackedConsumer` carries it into `ClickFact.UserId` — no extra lookup
anywhere in the chain), so this is additive, not a new network hop.

**404, not 403, is deliberate.** A caller who doesn't own a hash gets exactly the same response as a
caller querying a hash that never existed, on purpose — this endpoint can't be used to probe which
hashes belong to other users. An anonymously-created link (`user_id IS NULL` on every one of its
click rows) has no reporting view for *anyone*, not a shared public one either — `NULL`-owner rows
never match any caller's `user_id = callerId` filter, and are explicitly excluded from the
"owned by someone else" check too, so every caller sees the same all-zero `200`, never real data
and never a `404`.

**Live top links**, three pieces working together:

1. **`TopLinksHub`** (`/hub/top-links`, SignalR) — on connect, adds the caller to a per-user group
   and immediately sends the current list (so the dashboard isn't empty until the next click); on
   disconnect, tells the scheduler below to forget this user once nobody's left watching them.
   Browsers can't set headers on a WebSocket, so the JWT arrives as `?access_token=`, read only for
   `/hub` paths (see `WebDefaults`).
2. **`TopLinksPushService`** (background service) — subscribes to Redis's `clicks:user:changed`
   pub/sub channel (`RedirectApi` publishes to it on every owned click) and marks the affected user
   dirty, but only if someone's actually watching them. A `PeriodicTimer` checks for due users every
   500ms and pushes at most once per 2 seconds per user, skipping a push whose payload is byte-for-
   byte identical to the last one sent — so a burst of clicks on the same link coalesces into one
   update, not one per click.
3. **`RedisTopLinksReader`** — on each push (or on connect), `ZUNIONSTORE`s the user's last 7 daily
   sorted-set buckets (written by `RedisClickCounterService` in RedirectApi) into a throwaway
   scratch key, reads the top 10 by score, and deletes the scratch key. A true sliding window,
   recomputed from the buckets on every read — not a snapshot that resets at midnight.

## How it's implemented here

```csharp
// src/backend/Services/ReportingApi/Realtime/TopLinksPushService.cs:84
private async Task PushAsync(Guid userId, CancellationToken cancellationToken)
{
    var top = await _reader.GetTopAsync(userId, DateTime.UtcNow, cancellationToken);
    var payload = JsonSerializer.Serialize(top);
    if (_scheduler.IsUnchanged(userId, payload)) return;

    _scheduler.RecordPayload(userId, payload);
    // Re-checked after recording - a disconnect that landed during the query above would
    // otherwise have its Forget() run before RecordPayload re-added this user's payload.
    if (!_subscriptions.IsWatched(userId))
    {
        _scheduler.Forget(userId);
        return;
    }

    await _hub.Clients.Group(TopLinksHub.GroupFor(userId))
        .SendAsync(TopLinksHub.TopLinksMethod, top, cancellationToken);
}
```

Both the reports endpoints and the top-links hub share one service process and one Redis/
ClickHouse connection pool each, but are otherwise independent — a ClickHouse outage doesn't affect
the live push path, and (since the resilience fixes below) a Redis outage doesn't take down the
report endpoints either.

## Pitfalls

- 🐛 [A CORS/credentials mismatch silently blocked the SignalR negotiate request](pitfall:signalr-cors-credentials-mismatch-blocks-negotiate)
  (infrastructure-bug) — fixed
- 🐛 [Per-user push state that never got evicted](pitfall:push-service-per-user-state-never-evicted)
  (memory-leak) — fixed
- 🐛 [A fail-open catch that only caught RedisException let timeouts fail the redirect](pitfall:fail-open-catch-too-narrow)
  (resilience-gap) — fixed
- 🐛 [A Redis blip at boot crashed the whole API](pitfall:redis-multiplexer-abort-on-connect-fail-crashes-host)
  (resilience-gap) — fixed
- 🐛 [A real-time connection hook gave up permanently after one bad start](pitfall:top-links-hook-gives-up-after-failed-connect)
  (resilience-gap) — fixed

## Relatives

### Nodes

- [ReportingService](node:reporting-service) — writes what the reports endpoints read
- [ClickHouse](node:clickhouse)
- [RedirectApi](node:redirect-api) — writes the Redis buckets this node's push path reads
- [Redis (master)](node:redis-master)

### Patterns

- [CQRS fan-out](pattern:cqrs-fanout)
