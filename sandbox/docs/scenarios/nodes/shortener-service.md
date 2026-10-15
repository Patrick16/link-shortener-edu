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
// src/backend/Services/ShortenerService/LinkCreatedConsumer.cs, HandleBatchAsync (trimmed)
await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
var poisonMessages = new List<PoisonMessage>();

// Same hash more than once in one batch: identical OriginalLink is an intra-batch redelivery
// (silently dropped), a different one is a genuine conflict (reported, see ClassifyAgainstExisting).
var distinctByHash = /* GroupBy(Hash), compare content within each group - see source */;

var existingLinks = await context.Links.Where(x => hashes.Contains(x.Hash)).ToDictionaryAsync(x => x.Hash, cancellationToken);
var newItems = ClassifyAgainstExisting(distinctByHash, existingLinks, poisonMessages);
context.Links.AddRange(newItems.Select(BuildEntity));

try { await context.SaveChangesAsync(cancellationToken); }
catch (DbUpdateException)
{
    // Re-check against Postgres - the sync-gRPC batch-of-one race actually resolves here, not
    // in the pre-check above (both racers see "not yet stored" before either commits).
    var nowExisting = await context.Links.Where(x => hashes.Contains(x.Hash)).ToDictionaryAsync(x => x.Hash, cancellationToken);
    newItems = ClassifyAgainstExisting(newItems, nowExisting, poisonMessages);
    context.Links.AddRange(newItems.Select(BuildEntity));
    await context.SaveChangesAsync(cancellationToken);
}

return poisonMessages.Count > 0 ? BatchOutcome.WithPoison(poisonMessages) : BatchOutcome.Success;
```

`ClassifyAgainstExisting` is what actually implements the redelivery-safety rule: a hash with no
existing row is new; an existing row with the SAME `OriginalLink` is a true duplicate (silently
skipped); an existing row with a DIFFERENT `OriginalLink` is a genuine conflict, appended to
`poisonMessages` with a reason read off the *existing* (winning) row's `HasCustomAlias` - see
[Custom aliases](pattern:custom-aliases) for why that distinction has to live on the winning side.

As a `BackgroundService` (no per-request DI scope), it takes `IDbContextFactory<DatabaseContext>`
rather than the request-scoped `DatabaseContext` pattern the two web APIs use — see this node's
Pitfalls for what goes wrong if that distinction gets missed.

`shortener-service` is one of the two services (with `traffic-service`) that can be scaled to
multiple replicas via control-api — RabbitMQ round-robins deliveries across competing consumers on
the same queue, which is what actually turns one replica into many (raising prefetch alone doesn't;
prefetch only bounds unacked messages per channel, it doesn't create in-process concurrency). See
this node's Pitfalls for a race that scaling surfaced in code that had (at the time, correctly)
assumed it would only ever run as one instance.

`HandleBatchAsync` is also what [LinkApi](node:link-api)'s custom-alias path calls synchronously,
batch-of-one, over the same gRPC endpoint `Messaging:Mode=grpc` uses — this node has no separate
code path for it. Its redelivery-safety check now compares each conflicting row's `OriginalLink`,
not just whether the hash already exists, so a custom alias losing a uniqueness race is reported as
a conflict (`BatchOutcome.WithPoison`) instead of being silently absorbed the way a genuine
redelivery still is — see [Custom aliases](pattern:custom-aliases) for the full reasoning.

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
- ⚠️ [Intra-batch same-hash dedup silently dropped a genuine conflict, not just a redelivery](pitfall:shortener-batch-dedup-drops-conflicting-duplicate)
  (resilience-gap) — fixed

## Relatives

### Nodes

- [LinkApi](node:link-api) — publishes what this node consumes
- [RabbitMQ](node:rabbitmq)
- [links_db](node:links-db)

### Patterns

- [Async messaging](pattern:async-messaging)
- [Custom aliases](pattern:custom-aliases)
