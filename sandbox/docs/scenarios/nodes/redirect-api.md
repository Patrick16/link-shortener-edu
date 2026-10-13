## What it is

An ASP.NET Core Web API — resolves a short hash back to its original URL and redirects. The
highest-QPS endpoint in the whole system: created once per link, hit once per click.

## What it solves

The whole point of a link shortener is this one request being fast and cheap, however many times
it's called — see [Caching](pattern:caching) for why that means avoiding Postgres on the common
case.

## How it works

`GET /{hash}`: cache-aside lookup ([Caching](pattern:caching)), publish a `ClickTrackedEvent`
([Async messaging](pattern:async-messaging)), record the click in the owner's Redis day bucket, then a 302.

## How it's implemented here

```csharp
// src/backend/Services/RedirectApi/Controllers/RedirectController.cs:26
public async Task<IActionResult> RedirectToOrigin(string hash, CancellationToken ct)
{
    var link = await _cache.GetOrFetch(hash, fetchFromDb: Fetch, ct);
    if (link is null) return NotFound();

    var clickEvent = new ClickTrackedEvent { Id = Guid.NewGuid(), Hash = hash, /* ... */ };
    await _dispatcher.DispatchAsync(clickEvent, Topics.ClickTracked, ct);

    // Redis ZINCRBY into the owner's day bucket - no-op for anonymous links, never touches Postgres
    await _clickCounter.RecordClickAsync(hash, link.UserId, clickEvent.ClickedAt, ct);

    return Redirect(link.OriginalLink); // 302, never permanent - the target can change
}
```

Two separate consumers process `ClickTrackedEvent` downstream, for different reasons:
`ShortenerService` maintains `Links.ClickCount` as a fast display counter, `TrafficService` writes
the real audit trail (Postgres `Clicks` + Mongo click metadata) — see
[ShortenerService](node:shortener-service)'s Pitfalls for a race this split surfaced.

`IClickCounterService` writes each owned click into a per-user, per-day sorted set
(`clicks:user:{userId}:day:{YYYYMMDD}`). ReportingApi reads those buckets for the live top-links
view. The request-response cache (`link:{hash}`) is a separate key on the same Redis instance,
with a different purpose: it's disposable, the day buckets are a rolling aggregate.

## Pitfalls

- 🐛 [Every publish opened and declared a brand-new AMQP channel](pitfall:rabbitmq-publisher-unpooled-channel-hot-path)
  (cpu-leak) — fixed
- ⚠️ [Click counter incremented before the dispatch it should depend on](pitfall:click-counter-incremented-before-dispatch)
  (resilience-gap) — fixed
- ⚠️ [Point-to-point RPC dispatch silently breaks pub/sub fan-out to the other consumers](pitfall:sync-dispatch-breaks-pubsub-fanout)
  (architecture-bug) — known limitation
- ⚠️ [RabbitMQ's publisher stack started even when the active transport doesn't use it](pitfall:rabbitmq-publisher-started-regardless-of-messaging-mode)
  (performance) — fixed
- 🐛 [A read immediately after a write can land on a lagging replica](pitfall:pgcat-read-your-writes-race)
  (race-condition) — fixed; this node's cache-first read is what the fix relies on
- 🐛 [Three services ran identical caching code against three disjoint Redis keyspaces](pitfall:redis-instance-name-breaks-cross-service-cache-sharing)
  (coupling) — fixed; this node's own cache reads were invisible to LinkApi/ShortenerService's writes
- ⚠️ [Fail-open that only caught RedisException let timeouts fail the redirect](pitfall:fail-open-catch-too-narrow)
  (resilience-gap) — fixed

## Relatives

### Nodes

- [LinkApi](node:link-api) — the hashes this node resolves come from there
- [ShortenerService](node:shortener-service) — see the click-tracking split note above
- [TrafficService](node:traffic-service) — see the click-tracking split note above
- [Redis](node:redis-master) — cache and per-user click day buckets
- [RabbitMQ](node:rabbitmq) — carries `ClickTrackedEvent`

### Patterns

- [Caching](pattern:caching)
- [Async messaging](pattern:async-messaging)
