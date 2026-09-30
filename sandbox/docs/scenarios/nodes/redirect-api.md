## What it is

An ASP.NET Core Web API — resolves a short hash back to its original URL and redirects. The
highest-QPS endpoint in the whole system: created once per link, hit once per click.

## What it solves

The whole point of a link shortener is this one request being fast and cheap, however many times
it's called — see [Caching](pattern:caching) for why that means avoiding Postgres on the common
case.

## How it works

`GET /{hash}`: cache-aside lookup ([Caching](pattern:caching)), a Redis-backed click counter
increment, publish a `ClickTrackedEvent` ([Async messaging](pattern:async-messaging)), then a 302.

## How it's implemented here

```csharp
// src/backend/Services/RedirectApi/Controllers/RedirectController.cs:26
public async Task<IActionResult> RedirectToOrigin(string hash, CancellationToken ct)
{
    var link = await _cache.GetOrFetch(hash, fetchFromDb: Fetch, ct);
    if (link is null) return NotFound();

    await _clickCounter.IncrementAsync(hash, ct); // Redis INCR, cheap, doesn't touch Postgres

    var clickEvent = new ClickTrackedEvent { Id = Guid.NewGuid(), Hash = hash, /* ... */ };
    await _publisher.PublishAsync(clickEvent, Topics.ClickTracked, ct);

    return Redirect(link.OriginalLink); // 302, never permanent - the target can change
}
```

Two separate consumers process `ClickTrackedEvent` downstream, for different reasons:
`ShortenerService` maintains `Links.ClickCount` as a fast display counter, `TrafficService` writes
the real audit trail (Postgres `Clicks` + Mongo click metadata) — see
[ShortenerService](node:shortener-service)'s Pitfalls for a race this split surfaced.

The click counter (`IClickCounterService`, a Redis `INCR` under `link:{hash}:clicks`) is
independent of the request-response cache (`link:{hash}`) — same Redis instance, different key,
different purpose: one is authoritative-ish (best-effort, synced from Postgres separately), the
other is disposable.

## Pitfalls

- 🐛 [Every publish opened and declared a brand-new AMQP channel](pitfall:rabbitmq-publisher-unpooled-channel-hot-path)
  (cpu-leak) — fixed

## Relatives

### Nodes

- [LinkApi](node:link-api) — the hashes this node resolves come from there
- [ShortenerService](node:shortener-service) — see the click-tracking split note above
- [TrafficService](node:traffic-service) — see the click-tracking split note above
- [Redis](node:redis-master) — cache and click counter
- [RabbitMQ](node:rabbitmq) — carries `ClickTrackedEvent`

### Patterns

- [Caching](pattern:caching)
- [Async messaging](pattern:async-messaging)
