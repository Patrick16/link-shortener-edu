# The SQLite fallback retry loop could crash itself permanently

**Category:** resilience-gap **Status:** fixed

Watch for a recovery mechanism that is itself not resilient to the exact kind of transient failure
it exists to recover from. `RabbitMqRetryWorker` (retries messages saved to the SQLite fallback
store while RabbitMQ was unreachable) ran its whole poll tick — `GetPendingAsync` /
`TryRepublishAsync` / `DeleteAsync` — directly inside the loop with no `try`/`catch`.
`SqliteMessageFallbackStore` opens a fresh `SqliteConnection` per call against the same on-disk
file, written to concurrently by the publisher's own fallback-save path. Any transient failure
there (e.g. `SQLITE_BUSY` from a concurrent write) propagates out of the loop and ends
`ExecuteAsync` — and a `BackgroundService` whose `ExecuteAsync` faults does not restart itself. The
retry mechanism goes silently, permanently dead for the rest of the process's life, even after
RabbitMQ recovers — the one piece of infrastructure whose whole job is surviving outages becomes a
single point of failure itself.

⚠️ **Mistake** — no exception handling around the tick body:

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    using var timer = new PeriodicTimer(PollInterval);
    while (await timer.WaitForNextTickAsync(stoppingToken))
    {
        var pending = await _fallbackStore.GetPendingAsync(stoppingToken);
        foreach (var message in pending)
        {
            if (await _publisher.TryRepublishAsync(message, stoppingToken))
                await _fallbackStore.DeleteAsync(message.Id, stoppingToken);
        }
    }
}
```

✅ **Do this instead** — move the tick body into its own method, wrapped so a transient failure
logs a warning instead of ending the host:

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    using var timer = new PeriodicTimer(PollInterval);
    while (await timer.WaitForNextTickAsync(stoppingToken))
    {
        try
        {
            await PollOnceAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A transient failure here (e.g. the SQLite fallback file briefly locked by another
            // replica) must not crash the whole host.
            _logger.LogWarning(ex, "Retry tick failed, will retry next interval");
        }
    }
}
```

The same commit also capped `GetPendingAsync`'s query (`LIMIT 500`) — unbounded, it loaded the
entire pending table into memory on every tick, serially republishing one row at a time, so a long
RabbitMQ outage made each tick run longer than the poll interval itself.

(see review-reports/2026-09-17-1656-c03d2a3-rabbitmq-retry-worker.md, F1/F2 — fixed in `dd6ac13`)

## Relatives

### Nodes

- [LinkApi](node:link-api)
- [RedirectApi](node:redirect-api)
- [RabbitMQ](node:rabbitmq)

### Patterns

- [Async messaging](pattern:async-messaging)
