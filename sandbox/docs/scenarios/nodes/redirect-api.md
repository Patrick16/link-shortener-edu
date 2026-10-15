## What it is

An ASP.NET Core Web API — resolves a short hash back to its original URL and redirects. The
highest-QPS endpoint in the whole system: created once per link, hit once per click.

## What it solves

The whole point of a link shortener is this one request being fast and cheap, however many times
it's called — see [Caching](pattern:caching) for why that means avoiding Postgres on the common
case.

## How it works

`GET /{hash}`: a Redis "deleted" tombstone check first, then cache-aside lookup
([Caching](pattern:caching)) with a live `DeletedAt`/`ExpiresAt` check on what comes back, then
(if still alive) publish a `ClickTrackedEvent` ([Async messaging](pattern:async-messaging)),
record the click in the owner's Redis day bucket, then a 302. A dead hash - whichever check caught
it - gets an HTML "gone" (`410`) or "not found" (`404`) page instead, not this API's usual JSON
`ProblemDetails` shape; see below for why that's a deliberately different response type.

## How it's implemented here

```csharp
// src/backend/Services/RedirectApi/Controllers/RedirectController.cs:26
public async Task<IActionResult> RedirectToOrigin(string hash, CancellationToken ct)
{
    var marker = await _deletionMarkers.GetAsync(hash, ct);
    if (marker is not null) return GonePage(marker.Reason, marker.DeletedAt);

    var link = await _cache.GetOrFetch(hash, fetchFromDb: Fetch, ct);
    if (link is null) return NotFoundPage();

    if (link.DeletedAt is not null || link.ExpiresAt <= DateTime.UtcNow)
    {
        return GonePage(/* ... */);
    }

    var clickEvent = new ClickTrackedEvent { Id = Guid.NewGuid(), Hash = hash, /* ... */ };
    await _dispatcher.DispatchAsync(clickEvent, Topics.ClickTracked, ct);

    // Redis ZINCRBY into the owner's day bucket - no-op for anonymous links, never touches Postgres
    await _clickCounter.RecordClickAsync(hash, link.UserId, clickEvent.ClickedAt, ct);

    return Redirect(link.OriginalLink); // 302, never permanent - the target can change
}
```

The tombstone check (`ILinkDeletionMarkerStore.GetAsync`, Redis key `link:deleted:{hash}` -
deliberately separate from the `link:{hash}` cache-aside entry) runs before anything else: once a
hash is tombstoned, nothing else about the record matters for this decision, and it skips a Redis
AND a potential Postgres round trip for a hash already known dead. It isn't the *only* check,
though - that tombstone is only guaranteed to exist once
[ShortenerService](node:shortener-service)'s consumer or sweep job has actually confirmed it, and
the sweep runs once a minute, so the live `DeletedAt`/`ExpiresAt` check on the `Link` row itself
closes the window a tombstone-only check would otherwise leave open for up to that whole minute.

The gone/not-found pages are plain inline HTML (`ContentResult`, explicit status code), the first
HTML response anywhere in `src/backend` - a short-link redirect is followed by a browser's
top-level navigation, not a script reading JSON, so a human needs something readable here instead
of the usual `{type,title,status,detail}` shape. Written with the body already populated
specifically so `UseApiExceptionHandling`'s `UseStatusCodePages` callback - which only fires on a
response that's still empty - leaves it alone; verified against the running stack, not just
assumed from the ASP.NET Core docs.

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
- ⚠️ [A fail-open tombstone write is not enough on its own to invalidate an already-cached entity](pitfall:deletion-tombstone-alone-leaves-stale-cache-entry)
  (resilience-gap) — fixed; this node's cache-hit read path is the one that stays fooled if the fix fails

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
