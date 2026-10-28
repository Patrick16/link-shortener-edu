# A reporting query ran five independent round trips sequentially, one of them redundant

**Category:** db-query **Status:** fixed

Two related mistakes in the same method, on the same hot read path (`GET /reports/{hash}/summary`,
[ReportingApi](node:reporting-api)'s `GetSummaryAsync`), worth watching for together since fixing
one makes the other's fix cheaper:

1. **Independent queries awaited one at a time instead of run concurrently.** `byDay`, `byCountry`,
   `byDevice`, `byBrowser` have no data dependency on each other — each is its own `GROUP BY` over
   the same `clicks` table, filtered to the same `hash`/`user_id`. Awaiting each in turn before
   starting the next means the endpoint's total latency is the *sum* of four round trips to
   ClickHouse instead of the slowest one.
2. **A fifth query recomputed a number the other four already contained.** A separate `total`
   query ran its own full filtered scan (`SELECT count() FROM clicks WHERE hash = ... AND user_id =
   ...`) to get the overall click count — but `byDay` already groups every row matching that exact
   same filter by date; summing its counts gives the identical total without a second scan.

Together: 5 sequential round trips to answer one summary request, one of which did work the other
four had already done. Running the four real queries concurrently (via `IHttpClientFactory`-pooled
connections — see [the ClickHouse connection-pooling
pitfall](pitfall:clickhouse-unpooled-httpclient-per-call) this fix depended on being safe first) and
deriving the total from `byDay` in memory cuts this to 4 concurrent round trips with no `total`
query at all.

**No real before/after diff exists for this one** — caught and fixed in the same uncommitted
working-tree session the feature was built in, before the first commit. Reconstructed from the
finding's own description, not pulled from `git show`.

⚠️ **Mistake** — sequential awaits, plus a fifth redundant query:

```csharp
// reconstructed shape, not a real diff — see note above
public async Task<ClickSummary> GetSummaryAsync(string hash, Guid callerUserId, CancellationToken ct)
{
    var total = await RunScalarAsync("SELECT count() FROM clicks WHERE hash = {hash:String} AND user_id = toUUID({userId:String})", hash, callerUserId, ct);
    var byDay = await RunBucketsAsync(/* GROUP BY date */, hash, callerUserId, ct);
    var byCountry = await RunTopBucketsAsync("country", hash, callerUserId, ct);
    var byDevice = await RunTopBucketsAsync("device_type", hash, callerUserId, ct);
    var byBrowser = await RunTopBucketsAsync("browser", hash, callerUserId, ct);

    return new ClickSummary(hash, total, byDay, byCountry, byDevice, byBrowser);
}
```

✅ **Do this instead** — `src/backend/Shared/Infrastructure/ClickHouseClickFactQueryService.cs:28-45`,
`GetSummaryAsync`: start all four real queries, `Task.WhenAll` them, derive the total from `byDay`:

```csharp
var byDayTask = RunBucketsAsync(
    "SELECT toString(toDate(clicked_at)) AS k, count() AS c FROM clicks WHERE hash = {hash:String} AND user_id = toUUID({userId:String}) GROUP BY k ORDER BY k",
    hash, callerUserId, cancellationToken);
var byCountryTask = RunTopBucketsAsync("country", hash, callerUserId, cancellationToken);
var byDeviceTask = RunTopBucketsAsync("device_type", hash, callerUserId, cancellationToken);
var byBrowserTask = RunTopBucketsAsync("browser", hash, callerUserId, cancellationToken);

await Task.WhenAll(byDayTask, byCountryTask, byDeviceTask, byBrowserTask);

var byDay = await byDayTask;

// No separate "SELECT count()" query - byDay already has no filter beyond hash/owner, so it
// already carries every row needed to derive the same total (found during review: the old
// version paid for an extra full filtered scan just to recompute a number already in hand).
var total = byDay.Sum(b => b.Count);

return new ClickSummary(hash, total, byDay, await byCountryTask, await byDeviceTask, await byBrowserTask);
```

Running four queries concurrently against the same underlying connection factory is only safe
because each call opens its own `ClickHouseConnection` wrapper (ADO.NET connections can't be
shared across concurrent calls) over a shared, pooled `HttpMessageHandler` — see the
connection-pooling pitfall linked above; this fix would have been unsafe to make before that one.

(see review-reports/2026-10-05-2356-cqrs-reporting-clickhouse.md, F7+F8 — fixed before first
commit, no diffable hash; see note above)

## Relatives

### Nodes

- [ReportingApi](node:reporting-api)

### Patterns

- [CQRS fan-out](pattern:cqrs-fanout)
