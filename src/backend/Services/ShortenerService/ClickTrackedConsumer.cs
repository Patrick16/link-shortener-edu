using Contracts.Events;
using Infrastructure;

namespace ShortenerService;

// Consumes batches of ClickTrackedEvent and buffers each matching hash's increment into
// ClickCountAggregator rather than writing Links.ClickCount directly - see that class's own
// comment for why (row-lock contention under multiple replicas; ClickCountFlushWorker does the
// actual Postgres write on its own ~500ms cadence, decoupled from this consumer's own batch size).
// This is a display counter, not the audit trail (TrafficService's own Clicks table, keyed by event
// Id, is the exactly-once source of truth for that) - on the rare redelivery after a consumer
// crash/nack, this counter can drift a click or two high. Not worth an idempotency table for a
// number that only needs to be approximately right.
public sealed class ClickTrackedConsumer(
    IMessageConsumer consumer,
    ClickCountAggregator aggregator,
    ILogger<ClickTrackedConsumer> logger) : BackgroundService
{
    private const string QueueName = "shortener-service.click-tracked";

    private readonly IMessageConsumer _consumer = consumer;
    private readonly ClickCountAggregator _aggregator = aggregator;
    private readonly ILogger<ClickTrackedConsumer> _logger = logger;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        _consumer.ConsumeBatchAsync<ClickTrackedEvent>(QueueName, Topics.ClickTracked, HandleBatchAsync, stoppingToken);

    internal Task<BatchOutcome> HandleBatchAsync(
        IReadOnlyList<BatchItem<ClickTrackedEvent>> batch, CancellationToken cancellationToken)
    {
        // Purely in-memory - no DB round trip here at all, which is the point (see
        // ClickCountAggregator's own comment). The batch is acked as soon as this returns, same as
        // before; what changed is that "durably applied" now happens later, on
        // ClickCountFlushWorker's own schedule, not synchronously with this ack.
        var countsByHash = batch
            .Select(x => x.Payload.Hash)
            .GroupBy(x => x)
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var (hash, count) in countsByHash)
        {
            _aggregator.Add(hash, count);
        }

        _logger.LogDebug("Buffered click-count increments for {Count} distinct hash(es) from this batch", countsByHash.Count);
        return Task.FromResult(BatchOutcome.Success);
    }
}
