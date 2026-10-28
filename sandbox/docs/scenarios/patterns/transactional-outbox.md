## What it is

The textbook fix for a non-transactional dual-write (two independent systems, no shared
transaction across them): instead of writing to System A then System B directly, write to System A
*plus a row describing the System B write* in one local transaction, then let a separate relay
worker read those rows and apply the System B write later, marking each row done once it succeeds.
The relay is at-least-once (it can re-apply a row it already applied), so the System B write has to
be idempotent either way - the outbox doesn't remove that requirement, it just moves *when* the
write happens from "inline, best-effort" to "guaranteed eventually, from a durable local record."

## What it solves

A plain dual-write (write A, then write B, no coordination) has one structural gap no amount of
retry logic inside the same request can close: if the process dies, is killed, or simply never gets
to the second write for any reason, after the first write has already committed, nothing durable
remembers that the second write still needs to happen. The outbox closes that specific gap by making
the "needs doing" fact itself durable and transactional with the first write - either both the real
write and the outbox row land together, or neither does.

**Considered and deliberately not built in this project** for
[TrafficService](node:traffic-service)'s `Clicks` (Postgres) + `ClickMeta` (Mongo) dual-write - see
"Why it isn't built here" below for why, and that node's own "Why no transactional outbox here"
note for the reasoning this pattern-doc exists to expand on.

## How it would work here

A toggle between this and today's plain dual-write would use this project's usual DI-swapped
interface convention (the same shape as `IEventDispatcher` for the messaging-mode toggle) - that's
just the switching mechanism, not the pattern itself, so it's not shown below. The outbox mechanism
itself is the point: a table in the same database as the first write, and a relay that drains it.

The outbox table would live in `clicks_db` (the database `TrafficService` already owns and is the
only migrator of - see this project's one-owning-service-per-database rule):

```sql
CREATE TABLE click_meta_outbox (
    id uuid PRIMARY KEY,         -- same Id as the Click row, written in the same transaction
    -- text, not json/jsonb: no field-level querying happens against this column (the relay always
    -- fetches the whole row and deserializes in-process), and the payload is always
    -- JsonSerializer.Serialize<T> output from a known type - never free-form/external input - so
    -- json/jsonb's structural-validation-on-insert buys nothing a real serializer bug wouldn't
    -- also break in some other, equally-undetected way; jsonb's extra reparse-into-binary cost
    -- would only pay for itself if something queried by field inside this column, which nothing does.
    payload text NOT NULL,       -- enough to rebuild the ClickMeta write without re-reading Clicks
    created_at timestamptz NOT NULL DEFAULT now(),
    published_at timestamptz NULL  -- NULL = still pending; set once the relay's Mongo write commits
);
```

**Where the row gets written, and how its transaction works.** Inside
`ClickTrackedConsumer.HandleBatchAsync`, right where the direct Mongo write currently happens (see
traffic-service.md's "How it's implemented here") - the `Click` row and its outbox row go into the
*same* `SaveChangesAsync` call on the *same* `DbContext`:

```csharp
// EF Core already wraps every entity change tracked by one DbContext into a single database
// transaction when SaveChangesAsync is called once - no explicit BeginTransactionAsync needed on
// this side (unlike the relay's own read side below, which does need one). Either both the Click
// row and its outbox row commit together, or - on any failure, including a crash - neither does;
// there's no window where one exists without the other.
context.Clicks.AddRange(newClicks.Select(e => new Click(e.Id, e.ClickedAt, e.InboundLink, e.OutboundLink, e.Hash)));

// The UAParser/geo-IP resolution still happens here, synchronously, same as today's direct Mongo
// write (see HandleBatchAsync's existing Parallel.ForEachAsync over GeoIpConcurrency) - this mode
// changes WHERE the resulting ClickMeta goes (a durable row instead of straight to Mongo), not
// when the enrichment work itself happens.
context.ClickMetaOutbox.AddRange(newClicks.Select(e => new ClickMetaOutboxRow(
    e.Id,
    JsonSerializer.Serialize(BuildClickMeta(e, geoResults, userAgentParser)),
    DateTime.UtcNow)));

await context.SaveChangesAsync(cancellationToken);
```

Only `newClicks` get an outbox row here, unlike the real consumer's Mongo write today, which
unconditionally re-attempts for *every* deduped click in the batch, redelivered or not (the fix for
`traffic-mongo-write-skipped-on-redelivery` - see this node's Pitfalls). That defense-in-depth is
no longer needed for this mode: because the `Click` row and its outbox row commit atomically in the
*same* `SaveChangesAsync` above, an already-stored `Click` is guaranteed to already have its outbox
row too, written back when it was first inserted - there's no independent-failure window left to
re-attempt against. That guarantee is exactly what the outbox buys over the current scheme, not a
dropped safeguard.

`ClickMetaOutboxRow` would be a plain EF entity (`Id`, `Payload`, `CreatedAt`, `PublishedAt`)
mapped to `click_meta_outbox` via a new `DbSet<ClickMetaOutboxRow> ClickMetaOutbox` on
`DatabaseContext`, migrated the same way `Clicks` already is (`TrafficService` stays the one owner
of `clicks_db` either way).

and a relay worker polling it. Shaped like this project's other periodic workers
(`ClickCountFlushWorker`, `RabbitMqRetryWorker`) - a `PeriodicTimer` loop, one tick's own failure
logged and left for the next tick rather than crashing the host - so it'd live as its own file,
`src/backend/Services/TrafficService/ClickMetaOutboxRelayWorker.cs`, registered unconditionally in
`TrafficServiceExtensions.AddTrafficServices` (`builder.Services.AddHostedService<
ClickMetaOutboxRelayWorker>()`) next to `ClickTrackedConsumer` - "unconditionally" because an empty
table is a cheap no-op poll, the same reasoning `RabbitMqRetryWorker` already runs regardless of
whether its own SQLite fallback currently has anything queued. Unlike `ClickCountFlushWorker`
(which needed a `StopAsync` override - see backlog item 8 - specifically because its buffered
counts only ever live in-process, in `ClickCountAggregator`), this worker deliberately skips one:
its "pending work" is already durable in `click_meta_outbox` itself (`published_at IS NULL`), so a
graceful stop just defers those rows to the next tick, this replica's restart, or another replica -
no data loss, only a latency bump bounded by `PollInterval`.

```csharp
public sealed class ClickMetaOutboxRelayWorker(
    IDbContextFactory<DatabaseContext> dbContextFactory,
    IClickMetaStore clickMetaStore,
    ILogger<ClickMetaOutboxRelayWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private const int BatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RelayOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A transient failure here (e.g. Mongo briefly unreachable) must not crash the
                // whole host - same reasoning as every other periodic worker in this codebase.
                logger.LogWarning(ex, "Outbox relay tick failed - will retry next tick");
            }
        }
    }

    internal async Task RelayOnceAsync(CancellationToken cancellationToken)
    {
        await using var context = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        // An explicit transaction, not three independent calls - FOR UPDATE's row lock is only
        // held until the transaction that acquired it ends. Without one, EF would commit the
        // SELECT as its own implicit transaction, releasing the lock immediately - a second relay
        // replica's SKIP LOCKED would then see these same rows as unlocked and race to relay them
        // too, defeating the whole point of SKIP LOCKED. The cost isn't just "a bit longer": this
        // transaction - its row locks AND its Postgres connection, drawn from the same pool
        // ClickTrackedConsumer's own writes use - stays open for the Mongo round trip's full
        // duration too. If Mongo is down or the driver hangs with no call bounded by anything
        // shorter than host shutdown, these exact rows (and one connection-pool slot) are starved
        // indefinitely, not just delayed - see the bounded Mongo call below for why that's not
        // left unbounded.
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var rows = await context.Database.SqlQuery<OutboxRow>($"""
            SELECT id, payload FROM click_meta_outbox
            WHERE published_at IS NULL
            ORDER BY created_at
            LIMIT {BatchSize}
            FOR UPDATE SKIP LOCKED
            """).ToListAsync(cancellationToken);

        if (rows.Count == 0) return;

        // Bounded, not the raw stoppingToken (which only cancels on host shutdown) - without this,
        // a hung Mongo driver call would hold the transaction above open indefinitely instead of
        // just "longer" (see that comment). Same linked-CancellationTokenSource-plus-CancelAfter
        // shape this project already uses for RedirectApi's own request budget, applied to a
        // background tick instead of an HTTP request.
        using var mongoTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        mongoTimeout.CancelAfter(TimeSpan.FromSeconds(10));

        // At-least-once, not exactly-once: SaveManyAsync's upsert-by-Id (MongoClickMetaStore)
        // is what makes it safe to re-relay a row that was already applied but whose own
        // published_at UPDATE below never committed (e.g. this timeout firing, or a crash between
        // the two statements).
        var metas = rows.Select(r => JsonSerializer.Deserialize<ClickMeta>(r.Payload)!);
        await clickMetaStore.SaveManyAsync(metas, mongoTimeout.Token);

        var ids = rows.Select(r => r.Id).ToArray();
        await context.Database.ExecuteSqlAsync($"""
            UPDATE click_meta_outbox SET published_at = now() WHERE id = ANY({ids})
            """, cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation("Relayed {Count} outbox row(s) to Mongo", rows.Count);
    }

    private sealed record OutboxRow(Guid Id, string Payload);
}
```

## Why it isn't built here

The obvious demo for a toggle like this is a deterministic fault hook (e.g.
`FAULT_INJECT_AFTER_PG_COMMIT=1`) that kills the process mid-dual-write, with dual-write mode
expected to show permanent drift and outbox mode expected to show none. Worked through in detail
instead of built, because that specific demo doesn't actually show anything against *this*
consumer:

`RabbitMqConsumer` only acks a batch *after* `HandleBatchAsync` returns `BatchOutcome.Success` -
both the Postgres and Mongo writes have to complete first (see
[Async messaging](pattern:async-messaging)). A fault hook that kills the process right after the
Postgres commit, before the Mongo write, leaves that delivery **unacked**. On restart, RabbitMQ
redelivers the whole batch; the redelivered Postgres inserts are skipped (already stored), but the
Mongo write is unconditionally re-attempted for every click in the batch and succeeds this time.
Dual-write mode self-heals via the exact same broker-redelivery mechanism an outbox relay would
also need a restart to recover through - no drift ever becomes observable, in either mode, for
this fault.

To make the two modes genuinely diverge, the fault would have to bypass the broker's own
redelivery safety net entirely - e.g. a standalone test harness calling the write path directly for
one click, with no RabbitMQ consumption around it at all. There, dual-write mode really would show
a Postgres row with no matching `ClickMeta`
forever (nothing left to redeliver it), while outbox mode would still have a durable,
same-transaction outbox row a relay could eventually pick up. That's a true, demonstrable property
of the pattern in isolation - but it isn't a gap in *this consumer's* actual, already-
redelivery-safe pipeline. Building the toggle to re-discover that live wouldn't change the
conclusion `traffic-service.md`'s own "Why no transactional outbox here" note already reaches: an
outbox here would close the same narrow "stuck thread, no dropped connection" gap that note already
names as not meaningfully improved by one anyway (it would hang the relay just as easily) - at the
cost of a new table, a new worker, and extra latency on the enrichment write.

## Pitfalls

None recorded - this pattern was designed on paper and deliberately not built here, so there's no
real before/after to show.

## Relatives

### Nodes

- [TrafficService](node:traffic-service) — the dual-write this pattern was evaluated against and
  not applied to

### Patterns

- [Async messaging](pattern:async-messaging) — the broker-redelivery mechanism that already closes
  most of the gap this pattern would otherwise close
