## What it is

A .NET Worker Service (no HTTP surface beyond `/health/live`/`/health/ready`) — consumes
`LinkCreatedEvent` and persists links to `links_db`. Also consumes `ClickTrackedEvent` to maintain
`Links.ClickCount`, a separate concern from [TrafficService](node:traffic-service)'s audit trail.

## What it solves

LinkApi returns a hash before anything is written to Postgres — something has to actually do that
write. See [Async messaging](pattern:async-messaging) for why this is a separate process instead
of LinkApi writing directly.

## How it works

Consumes `LinkCreatedEvent` in batches, redelivery-safe (a hash already stored is skipped rather
than failing the whole batch on a unique-key violation). See [Async messaging](pattern:async-messaging)
for the publish side and the SQLite fallback queue this consumer's redelivery-safety cooperates
with.

## How it's implemented here

```csharp
// src/backend/Services/ShortenerService/LinkCreatedConsumer.cs:25
internal async Task<BatchOutcome> HandleBatchAsync(
    IReadOnlyList<BatchItem<LinkCreatedEvent>> batch, CancellationToken cancellationToken)
{
    await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

    var distinctByHash = batch.Select(x => x.Payload).GroupBy(x => x.Hash).Select(g => g.Last()).ToList();
    var alreadyStored = await context.Links
        .Where(x => distinctByHash.Select(e => e.Hash).Contains(x.Hash))
        .Select(x => x.Hash).ToHashSetAsync(cancellationToken);

    var newLinks = distinctByHash.Where(x => !alreadyStored.Contains(x.Hash));
    context.Links.AddRange(newLinks.Select(e => new Link(e.Hash, e.OriginalLink, e.ShortenLink, e.CreatedAt, e.UserId)));
    await context.SaveChangesAsync(cancellationToken);
    return BatchOutcome.Success;
}
```

As a `BackgroundService` (no per-request DI scope), it takes `IDbContextFactory<DatabaseContext>`
rather than the request-scoped `DatabaseContext` pattern the two web APIs use — see this node's
Pitfalls for what goes wrong if that distinction gets missed.

`shortener-service` is one of the two services (with `traffic-service`) that can be scaled to
multiple replicas via control-api — RabbitMQ round-robins deliveries across competing consumers on
the same queue, which is what actually turns one replica into many (raising prefetch alone doesn't;
prefetch only bounds unacked messages per channel, it doesn't create in-process concurrency). See
this node's Pitfalls for a race that scaling surfaced in code that had (at the time, correctly)
assumed it would only ever run as one instance.

## Pitfalls

- 🐛 [AddDbContextPool registered as a scoped service inside a Worker Service](pitfall:shortener-dbcontext-pool-scoped-in-worker)
  (connection-pooling) — fixed
- 🐛 [Click-count increment raced once the consumer became scalable](pitfall:shortener-click-count-race)
  (race-condition) — fixed
- ⚠️ [Point-to-point RPC dispatch silently breaks pub/sub fan-out to the other consumers](pitfall:sync-dispatch-breaks-pubsub-fanout)
  (architecture-bug) — known limitation
- 🐛 [Three services ran identical caching code against three disjoint Redis keyspaces](pitfall:redis-instance-name-breaks-cross-service-cache-sharing)
  (coupling) — fixed; this node's own Redis pre-warm (`LinkCreatedConsumer.cs`, not shown in the
  snippet above) never actually reached RedirectApi's cache before this fix

## Relatives

### Nodes

- [LinkApi](node:link-api) — publishes what this node consumes
- [RabbitMQ](node:rabbitmq)
- [links_db](node:links-db)

### Patterns

- [Async messaging](pattern:async-messaging)
