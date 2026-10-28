## What it is

A .NET Worker Service (no HTTP surface beyond `/health/live`/`/health/ready`) — consumes
`LinkCreatedEvent` and persists links to `links_db`. Also consumes `ClickTrackedEvent` to maintain
`Links.ClickCount`, a separate concern from [TrafficService](node:traffic-service)'s audit trail.
Also owns the two sides of deletion: consuming `LinkDeletedEvent` (manual delete, triggered from
[LinkApi](node:link-api)) and a once-a-minute sweep job that expires links past their `ExpiresAt`.

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

### Deletion: one flow, two triggers

Manual delete and TTL expiry end up doing the exact same thing to a row (`DeletedAt = now()` +
confirm a Redis tombstone), but they arrive through different doors. `LinkDeletedConsumer`
consumes batches of `LinkDeletedEvent` from RabbitMQ (published by LinkApi's `DELETE` endpoint,
through the same `IEventDispatcher`/`Messaging:Mode` toggle `LinkCreatedEvent` uses - no forced
transport here, unlike custom aliases). `LinkExpiryWorker` runs *in this same process* on a
`PeriodicTimer`, once a minute, and goes straight to Postgres with its own `UPDATE ... RETURNING`
- there's no cross-service hop to make, since the process doing the sweep already owns `links_db`,
so routing it through RabbitMQ would only make it slower and more failure-prone for no benefit.

```csharp
// src/backend/Services/ShortenerService/LinkDeletedConsumer.cs, HandleBatchAsync (trimmed)
var existingHashes = await context.Links.Where(x => hashes.Contains(x.Hash)).Select(x => x.Hash).ToHashSetAsync(ct);

await context.Links
    .Where(x => hashes.Contains(x.Hash) && x.DeletedAt == null)
    .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.DeletedAt, now), ct);

foreach (var hash in existingHashes)
{
    await _deletionMarkers.ConfirmAsync(hash, DeletedReason.Manual, now, ct);
    await _cache.InvalidateCacheAsync(hash);
}

var missingHashes = hashes.Where(h => !existingHashes.Contains(h)).ToList();
if (missingHashes.Count > 0) throw new InvalidOperationException(/* ... */);
```

`ExecuteUpdateAsync` - the same bulk-update primitive `ClickTrackedConsumer` already uses for
`ClickCount` - sidesteps having to load-then-mutate `Link.DeletedAt`, an `init`-only record
property. The `missingHashes` throw exists for a real race: LinkApi's own optimistic cache write
means a `DELETE` request can be accepted (and this event published) for a hash whose
`LinkCreatedEvent` hasn't been consumed yet - RabbitMQ gives no cross-topic ordering guarantee.
Throwing hands it to `RabbitMqConsumer`'s existing bounded delivery-attempt retry (with backoff)
instead of silently dropping it - unlike `ClickTrackedConsumer`'s own "no row found" case (which
just logs and moves on, since a missed click-count increment is an approximate display number by
design), a dropped delete would leave the link alive forever despite its owner asking for it to be
deleted, with nobody ever finding out.

`LinkExpiryWorker` follows `PartitionMaintenanceWorker`'s exact shape (see
[TrafficService](node:traffic-service)): same session-scoped Postgres advisory lock pattern
(`"shortener-service:link-expiry"`), same direct-not-through-pgcat connection, same
`OpenConnectionAsync`/`CloseConnectionAsync` pinning around the lock's acquire/release. Its
`UPDATE ... RETURNING "Hash"` both flips `DeletedAt` and tells the worker exactly which hashes it
just touched, so it can confirm a `DeletedReason.Expired` tombstone for each - the `DeletedAt IS
NULL` guard in its `WHERE` clause means a hash a manual delete reached first simply isn't among the
rows returned, so the two triggers can never double-process the same row.

**Both triggers also invalidate the main `link:{hash}` cache-aside entry** (`IEntityCacheService<Link>
.InvalidateCacheAsync`), alongside - not instead of - confirming the tombstone above. Found in
review: the tombstone alone wasn't enough, because `RedirectApi`'s live `DeletedAt`/`ExpiresAt`
check on a cache-aside `Link` only actually runs on a cache *miss* - a `Link` already warm in the
cache before being deleted or expired has `DeletedAt == null` baked in forever and keeps resolving
successfully for up to the cache entry's full remaining TTL (up to an hour by default) if the
tombstone confirm specifically fails. The two writes stay independent, not transactional with each
other - either one succeeding alone is now enough to close the gap it closes. See this node's
Pitfalls.

### Click-count aggregation: buffering writes to ease hot-key row-lock contention

`ClickTrackedConsumer`'s `ClickCount` maintenance (above) used to run one atomic
`ExecuteUpdateAsync` per consumed batch, straight against Postgres — correct (no lost-update race),
but every `ShortenerService` replica still issued its own `UPDATE` against the same row whenever a
link goes viral, and more replicas meant *more* lock contention on that one hot row, not less
(measured live: throughput went **down**, 190 → 76 msg/s, going from 1 to 4 replicas on a single
hot key).

`ClickCountAggregator` (a singleton `ConcurrentDictionary<string, long>`) and
`ClickCountFlushWorker` (a `BackgroundService` on a flat, deliberately non-jittered 500ms
`PeriodicTimer` — see [Async messaging](pattern:async-messaging)'s retry-jitter note for why this
one doesn't need it: each replica only ever flushes hashes *it itself* buffered, no cross-replica
correlation to decorrelate) decouple the two: `ClickTrackedConsumer.HandleBatchAsync` now just
calls `aggregator.Add(hash, count)` per distinct hash in the batch and acks immediately, with no DB
round trip at all. The flush worker drains the aggregator every tick and issues one `UPDATE` per
distinct hash *per tick*, not per batch — collapsing however many batches land for one hot hash
within a ~500ms window into a single write. **This reduces lock *frequency*, it does not eliminate
cross-replica contention** — with N replicas each on their own unsynchronized timer, a sustained
hot key can still see up to N competing `UPDATE`s roughly every 500ms; describe this as reducing
write frequency, not as "fixing" hot-key contention.

The tradeoff this buffering introduces: `ClickCount` is explicitly an approximate display number
(the real audit trail is [TrafficService](node:traffic-service)'s `Clicks` table), but acking a
batch as soon as its counts are buffered — instead of after a durable write — widens the loss
window on termination from "one batch, on an abrupt crash" to "up to one flush interval's worth of
increments, across every hash touched in that window, on *any* termination before the next flush."
See this node's Pitfalls for why that widened window had to be closed for the *ordinary, routine*
graceful-stop case specifically (restart, rolling deploy, a control-api scale-down) — an abrupt
kill (SIGKILL, OOM) still loses whatever was buffered and not yet flushed, same accepted tradeoff
as before.

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
- ⚠️ [A fail-open tombstone write is not enough on its own to invalidate an already-cached entity](pitfall:deletion-tombstone-alone-leaves-stale-cache-entry)
  (resilience-gap) — fixed
- ⚠️ [A buffered-write worker needs a flush on graceful stop, not just a documented "lost on crash" tradeoff](pitfall:clickcount-flush-worker-no-flush-on-shutdown)
  (resilience-gap) — fixed

## Relatives

### Nodes

- [LinkApi](node:link-api) — publishes what this node consumes
- [RedirectApi](node:redirect-api) — reads the `link:{hash}` cache entry this node's deletion
  consumer/sweep worker invalidate
- [RabbitMQ](node:rabbitmq)
- [links_db](node:links-db)

### Patterns

- [Async messaging](pattern:async-messaging)
- [Custom aliases](pattern:custom-aliases)
- [Caching](pattern:caching) — invalidates the main cache-aside entry on delete/expiry
