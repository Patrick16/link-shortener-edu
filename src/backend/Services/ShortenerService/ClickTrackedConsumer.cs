using Contracts.Events;
using Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ShortenerService;

// Consumes batches of ClickTrackedEvent and increments Links.ClickCount for each matching hash.
// This is a display counter, not the audit trail (TrafficService's own Clicks table, keyed by event
// Id, is the exactly-once source of truth for that) - on the rare redelivery after a consumer
// crash/nack, this counter can drift a click or two high. Not worth an idempotency table for a
// number that only needs to be approximately right.
public sealed class ClickTrackedConsumer(
    IMessageConsumer consumer,
    IDbContextFactory<DatabaseContext> dbContextFactory,
    ILogger<ClickTrackedConsumer> logger) : BackgroundService
{
    private const string QueueName = "shortener-service.click-tracked";

    private readonly IMessageConsumer _consumer = consumer;
    private readonly IDbContextFactory<DatabaseContext> _dbContextFactory = dbContextFactory;
    private readonly ILogger<ClickTrackedConsumer> _logger = logger;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        _consumer.ConsumeBatchAsync<ClickTrackedEvent>(QueueName, Topics.ClickTracked, HandleBatchAsync, stoppingToken);

    internal async Task<BatchOutcome> HandleBatchAsync(
        IReadOnlyList<BatchItem<ClickTrackedEvent>> batch, CancellationToken cancellationToken)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        // One atomic "UPDATE ... SET ClickCount = ClickCount + n" per distinct hash in the batch
        // instead of one per message - collapses a "hot hash" burst down to a single round trip per
        // hash. Still a plain ExecuteUpdateAsync (not a read-then-write), so concurrent replicas
        // updating the same hash still can't lose an increment to a race - see the git history for
        // why that matters here.
        var countsByHash = batch
            .Select(x => x.Payload.Hash)
            .GroupBy(x => x)
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var (hash, count) in countsByHash)
        {
            var updated = await context.Links
                .Where(x => x.Hash == hash)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ClickCount, x => x.ClickCount + count), cancellationToken);

            if (updated == 0)
            {
                // LinkCreatedEvent for this hash may not have been consumed yet (ordering across
                // queues isn't guaranteed), or the hash is simply unknown. Either way there's no row
                // to increment - log and move on rather than failing the whole batch.
                _logger.LogWarning("No link found for hash {Hash}, click count not incremented", hash);
            }
        }

        _logger.LogInformation("Applied click-count updates for {Count} distinct hash(es) from this batch", countsByHash.Count);
        return BatchOutcome.Success;
    }
}
