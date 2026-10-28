## What it is

A .NET Worker Service (no HTTP surface at all, unlike every other worker in this project, which at
least exposes `/health/live`/`/health/ready`) — a third, independent consumer of
`ClickTrackedEvent`, the same event [TrafficService](node:traffic-service) already consumes. Owns
`reports_db` in ClickHouse, the only writer against it.

## What it solves

See [CQRS fan-out](pattern:cqrs-fanout) for why this exists at all instead of querying Postgres
directly. Short version: the per-link stats panel ([ReportingApi](node:reporting-api) serves it)
wants aggregate, filterable queries (by day/country/device/browser) over potentially millions of
rows — exactly what a columnar OLAP store is built for, and exactly what running those same queries
against `clicks_db` would mean competing with TrafficService's own write traffic to answer.

## How it works

Binds its own queue (`reporting-service.click-tracked`) to the same `click.tracked` routing key
TrafficService already consumes — RabbitMQ fan-out, not a chain: [RedirectApi](node:redirect-api)'s
publish side is completely unaware this consumer exists. Each batch is deduplicated by event `Id`
(last-write-wins within the batch, matching TrafficService's own redelivery-safety reasoning), geo-
IP resolution runs with bounded parallelism (same `ip-api.com` rate-limit concern as TrafficService,
resolved **independently** — see this node's Pitfalls for the cost that doubling up had before it
was caught), and the whole batch is written with one bulk insert, never per-row.

**Why no row-level dedup against what's already in ClickHouse.** A redelivered event would normally
risk a duplicate row, but `reports_db.clicks` is a `ReplacingMergeTree` table ordered by
`(hash, id)` — Postgres has a real unique constraint for this, ClickHouse doesn't, by design, so
instead the engine itself collapses same-key rows down to one during a later background merge. This
consumer never queries ClickHouse before inserting (a point-lookup-per-row pattern ClickHouse is
specifically bad at) — it just lets the engine's own merge process eventually dedup, which means a
query run immediately after a redelivery can briefly still double-count until the next merge runs.

## How it's implemented here

```csharp
// src/backend/Services/ReportingService/ClickTrackedConsumer.cs:42
internal async Task<BatchOutcome> HandleBatchAsync(
    IReadOnlyList<BatchItem<ClickTrackedEvent>> batch, CancellationToken cancellationToken)
{
    var distinctById = batch.Select(x => x.Payload).GroupBy(x => x.Id).Select(g => g.Last()).ToList();

    var geoResults = new string?[distinctById.Count];
    await Parallel.ForEachAsync(
        Enumerable.Range(0, distinctById.Count),
        new ParallelOptions { MaxDegreeOfParallelism = GeoIpConcurrency, CancellationToken = cancellationToken },
        async (i, ct) => geoResults[i] = (await _geoIpResolver.ResolveAsync(distinctById[i].IpAddress, ct)).Country);

    var facts = /* ... build ClickFact per event, parsed UA + geo + referrer domain ... */;
    await _clickFactStore.InsertManyAsync(facts, cancellationToken);
    return BatchOutcome.Success;
}
```

`reports_db.clicks` schema and its `TTL` retention clause live in
`sandbox/infra/clickhouse/init.sql` — see [Data retention](pattern:data-retention-partitioning) for
why ClickHouse's own `TTL` mechanism looks nothing like Postgres's partitioning even though both
exist to solve the same "don't keep click data forever" problem.

## Pitfalls

- 🐛 [Two independent event consumers doubled load on a shared rate-limited external API](pitfall:geoip-dual-consumer-rate-limit-doubling)
  (architecture-bug) — fixed
- 🐛 [Shared-cache DI wiring copy-pasted across two independently deployed services](pitfall:geoip-cache-wiring-duplicated-across-services)
  (coupling) — fixed
- 🐛 [A ClickHouse client registered as a singleton still opened a fresh HttpClient on every call](pitfall:clickhouse-unpooled-httpclient-per-call)
  (connection-pooling) — fixed
- 🐛 [ClickHouse's healthcheck could pass before its init script finished creating the table](pitfall:clickhouse-healthcheck-passes-before-init-script-completes)
  (infrastructure-bug) — fixed

## Relatives

### Nodes

- [RedirectApi](node:redirect-api) — publishes what this node consumes
- [TrafficService](node:traffic-service) — the other, independent `ClickTrackedEvent` consumer
- [RabbitMQ](node:rabbitmq)
- [ClickHouse](node:clickhouse)
- [ReportingApi](node:reporting-api) — the only reader of what this node writes

### Patterns

- [CQRS fan-out](pattern:cqrs-fanout)
- [Async messaging](pattern:async-messaging)
- [Circuit breakers](pattern:circuit-breakers) — Redis breaker via `CachingGeoIpResolver`, plus the
  geo-IP bulkhead (concurrency limiter) on the same ip-api.com pipeline
