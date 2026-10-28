# A buffered-write worker needs a flush on graceful stop, not just a documented "lost on crash" tradeoff

**Category:** resilience-gap **Status:** fixed

Watch for this any time an in-memory buffer is drained periodically by a `BackgroundService`'s own
timer loop, and the design comment accepts "data buffered right before a crash is lost" as a known
tradeoff. That framing quietly assumes the *only* way the loop stops is a crash. In a system with a
control plane that routinely restarts, scales down, or redeploys services — all ordinary, frequent
operations, not rare failures — `PeriodicTimer.WaitForNextTickAsync` throws as soon as the host
cancels its token during an *ordinary* graceful shutdown too. If nothing hooks that shutdown path
to run one last drain, every routine stop silently loses however much was buffered — not the narrow
"one crash, one batch" window the design comment described, but a deterministic loss on every
single restart.

**Concrete instance in this project:** `ClickCountAggregator`/`ClickCountFlushWorker` buffer
click-count increments in-process and flush them to Postgres every 500ms, specifically to collapse
however many `ClickTrackedEvent` batches land for the same hot hash within that window into one
`UPDATE` instead of one per batch (see [ShortenerService](node:shortener-service)'s click-tracking
split). The aggregator's own comment named "the process crashes or is force-killed" as the loss
window — accurate as far as it went, but `ClickCountFlushWorker.ExecuteAsync` had no `try/finally`,
`StopAsync` override, or shutdown-lifetime hook draining the aggregator before the worker's loop
exited. A rolling redeploy, the control plane's "restart" button, or a scale-down from N replicas
to N-1 (all routine, frequently-exercised actions in this project) discarded whatever was buffered
for every hash that had received a click in the last (up to) 500ms — permanently: RabbitMQ had
already acked the batches that produced those counts, so nothing could redeliver them, and there
was no persistence or WAL to recover from. Before this buffering was introduced, the ack only
happened *after* the synchronous `ExecuteUpdateAsync` had durably applied the count, so a graceful
shutdown lost nothing at all — this change turned a rare, crash-only, single-batch loss window into
a routine, every-restart, whole-flush-interval loss.

⚠️ **Mistake** — `src/backend/Services/ShortenerService/ClickCountFlushWorker.cs` (before commit
`1780bc7`):

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    using var timer = new PeriodicTimer(FlushInterval);
    while (await timer.WaitForNextTickAsync(stoppingToken))
    {
        try
        {
            await FlushOnceAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Click-count flush tick failed - some buffered counts for this tick may be lost");
        }
    }
    // Loop exits as soon as stoppingToken is cancelled (PeriodicTimer throws
    // OperationCanceledException out of WaitForNextTickAsync) - nothing drains whatever
    // ClickCountAggregator is still holding at that moment, on an ordinary graceful stop just
    // as much as on a crash.
}
```

✅ **Do this instead** (`1780bc7`) — override `StopAsync`, let the base implementation finish
cancelling the loop first, then run one last flush using the host's own shutdown-deadline token
(not the already-cancelled `stoppingToken`) so the final write still gets a real chance to
complete:

```csharp
// Without this, every ordinary graceful stop (restart, rolling deploy, control-api
// scale-down - routine operations this project's own control plane drives regularly, not just
// a rare crash) would discard up to a full flush interval's worth of buffered counts across
// every pending hash, not the much narrower "one crash, one batch" window
// ClickCountAggregator's own comment accepts. base.StopAsync cancels the stoppingToken (ending
// ExecuteAsync's loop) and awaits that task before returning, so by the time this runs, no
// concurrent tick can still be mutating the aggregator - this is the one and only flush left to
// do. Uses `cancellationToken` (the host's own shutdown deadline, e.g. HostOptions.
// ShutdownTimeout), not the already-cancelled stoppingToken, so this final write still gets a
// real chance to complete instead of being cancelled before it starts.
public override async Task StopAsync(CancellationToken cancellationToken)
{
    await base.StopAsync(cancellationToken);

    try
    {
        await FlushOnceAsync(cancellationToken);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        _logger.LogWarning(ex, "Final click-count flush on shutdown failed - any still-buffered counts are lost");
    }
}
```

A regression test (`StopAsync_BufferedCountsNeverTicked_AreFlushedOnShutdownInstead`) starts the
real worker, buffers a count, and stops it well inside the 500ms tick interval — only a working
final-flush path makes it pass.

**Provenance:** found by `feature-review` on commit `aaad8cd`
(`.notes/review-reports/2026-10-28-0211-clickcount-aggregation.md`, F1), fixed in `1780bc7`.

## Relatives

### Nodes

- [ShortenerService](node:shortener-service)

### Patterns

- [Async messaging](pattern:async-messaging)
