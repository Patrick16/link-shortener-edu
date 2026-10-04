## What it is

A .NET Worker Service (no HTTP surface beyond `/health/live`/`/health/ready`) — consumes
`ClickTrackedEvent` and writes the click audit trail: the `Clicks` row in Postgres (`clicks_db`)
and a matching `ClickMeta` document in Mongo (`clicks_meta_db`), same `Id` on both.

## What it solves

A click needs to be recorded durably, but [RedirectApi](node:redirect-api) can't wait around for
two separate database writes before returning a 302 — see [Async messaging](pattern:async-messaging).
Splitting the record itself into two stores (Postgres for the core fact, Mongo for enrichment
data) is a separate decision from the async split — see "How it works" below.

## How it works

`Clicks` (Postgres) is the audit-trail source of truth: `Id`, `ClickedAt`, `InboundLink`,
`OutboundLink`, `Hash` — nothing that needs parsing. `ClickMeta` (Mongo) holds everything derived
from the request that triggered the click: raw `UserAgent`/`Referrer`, parsed browser/OS/device
(via `UAParser`, offline, no external calls), and geo (country/city via `ip-api.com`'s free
endpoint — best-effort, private/loopback addresses and any failure just leave geo fields `null`
rather than failing the click). Mongo is schema-less on purpose: this data grows independently of
the core record, and neither store is transactional with the other — see this node's Pitfalls for
what that non-transactionality cost before it was handled correctly.

This is a **separate consumer** from [ShortenerService](node:shortener-service)'s own
`ClickTrackedEvent` consumer, which only maintains `Links.ClickCount` as a fast display counter —
two consumers on the same event, for two different, deliberately independent reasons (a display
counter that can tolerate drift vs. a real audit trail that can't).

## How it's implemented here

```csharp
// src/backend/Services/TrafficService/ClickTrackedConsumer.cs:38
internal async Task<BatchOutcome> HandleBatchAsync(
    IReadOnlyList<BatchItem<ClickTrackedEvent>> batch, CancellationToken cancellationToken)
{
    // Postgres insert is skipped for already-stored ids (redelivery-safe) - but the Mongo write
    // below always runs for every click in the batch, redelivered or not. See this node's
    // Pitfalls for why that distinction matters.
    var newClicks = distinctByClickId.Where(x => !alreadyStored.Contains(x.Id));
    context.Clicks.AddRange(newClicks.Select(e => new Click(e.Id, e.ClickedAt, e.InboundLink, e.OutboundLink, e.Hash)));
    await context.SaveChangesAsync(cancellationToken);

    var metas = distinctByClickId.Select(e => new ClickMeta(
        e.Id, e.Hash, e.ClickedAt, e.UserAgent, e.Referrer, e.IpAddress,
        _userAgentParser.Parse(e.UserAgent).Browser, /* ... */,
        (await _geoIpResolver.ResolveAsync(e.IpAddress, cancellationToken)).Country, /* ... */));
    await _clickMetaStore.SaveManyAsync(metas, cancellationToken);
}
```

The Mongo write is independently idempotent (`MongoClickMetaStore` upserts by `Id`), which is what
makes "always attempt it, even on a Postgres-side redelivery" safe rather than duplicating data.

## Pitfalls

- 🐛 [Mongo ClickMeta write was permanently skipped after a Postgres-committed redelivery](pitfall:traffic-mongo-write-skipped-on-redelivery)
  (resilience-gap) — fixed
- 🐛 [Docker-internal IPs were misclassified as public, hitting a real external API on every click](pitfall:traffic-geoip-dualstack-misclassified)
  (cpu-leak) — fixed

## Relatives

### Nodes

- [RedirectApi](node:redirect-api) — publishes what this node consumes
- [ShortenerService](node:shortener-service) — the other, independent `ClickTrackedEvent` consumer
- [RabbitMQ](node:rabbitmq)
- [clicks_db](node:clicks-db)

### Patterns

- [Async messaging](pattern:async-messaging)
