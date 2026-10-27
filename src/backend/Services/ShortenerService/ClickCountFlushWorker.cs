using Microsoft.EntityFrameworkCore;

namespace ShortenerService;

// Periodically drains ClickCountAggregator and applies the buffered increments to Postgres - one
// UPDATE per distinct hash per tick, same ExecuteUpdateAsync shape ClickTrackedConsumer used to run
// synchronously per batch. Decoupling the write from the consumer's own batch cadence is the whole
// point (see ClickCountAggregator's own comment): however many batches land for the same hot hash
// within one ~500ms window now cost this worker a single UPDATE, not one per batch.
//
// A flat interval, not jittered like RabbitMqRetryWorker's poll delay (backlog item 7) - that
// worker needed jitter to decorrelate every REPLICA's retry from hitting the SAME broker at the
// same instant after an outage. There's no equivalent correlation risk here: each replica only
// ever flushes the hashes it itself buffered, so a predictable interval costs nothing.
public sealed class ClickCountFlushWorker(
    ClickCountAggregator aggregator,
    IDbContextFactory<DatabaseContext> dbContextFactory,
    ILogger<ClickCountFlushWorker> logger) : BackgroundService
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(500);

    private readonly ClickCountAggregator _aggregator = aggregator;
    private readonly IDbContextFactory<DatabaseContext> _dbContextFactory = dbContextFactory;
    private readonly ILogger<ClickCountFlushWorker> _logger = logger;

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
                // Whatever was already drained out of the aggregator for this tick but not yet
                // applied (e.g. a mid-loop Postgres error) is lost, not retried - same accepted
                // approximate-counter tradeoff as a process crash, just scoped to one tick's worth
                // instead of the whole buffer. Not worth re-buffering partial ticks for a number
                // that's explicitly allowed to drift.
                _logger.LogWarning(ex, "Click-count flush tick failed - some buffered counts for this tick may be lost");
            }
        }
    }

    internal async Task FlushOnceAsync(CancellationToken cancellationToken)
    {
        var pending = _aggregator.DrainPending();
        if (pending.Count == 0) return;

        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        foreach (var (hash, count) in pending)
        {
            var updated = await context.Links
                .Where(x => x.Hash == hash)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ClickCount, x => x.ClickCount + count), cancellationToken);

            if (updated == 0)
            {
                // LinkCreatedEvent for this hash may not have been consumed yet (ordering across
                // queues isn't guaranteed), or the hash is simply unknown. Either way there's no row
                // to increment - log and move on rather than failing the whole tick.
                _logger.LogWarning("No link found for hash {Hash}, click count not incremented", hash);
            }
        }

        _logger.LogDebug("Applied click-count updates for {Count} distinct hash(es)", pending.Count);
    }
}
