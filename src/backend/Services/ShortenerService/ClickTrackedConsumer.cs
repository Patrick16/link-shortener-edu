using Contracts.Events;
using Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ShortenerService;

// Consumes ClickTrackedEvent and increments Links.ClickCount for the matching hash. This is a
// display counter, not the audit trail (TrafficService's own Clicks table, keyed by event Id, is
// the exactly-once source of truth for that) - on the rare redelivery after a consumer
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
        _consumer.ConsumeAsync<ClickTrackedEvent>(QueueName, Topics.ClickTracked, HandleAsync, stoppingToken);

    internal async Task HandleAsync(ClickTrackedEvent @event, CancellationToken cancellationToken)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        // A single atomic "UPDATE ... SET ClickCount = ClickCount + 1" instead of a read-then-write
        // through the change tracker - this used to be read-then-write specifically because Link is
        // an immutable record with no public setter, but that shape raced for real once this
        // consumer became scalable to multiple replicas (control-api can now run several): two
        // replicas incrementing the same hot hash could both read the same starting value and one
        // increment would be lost. Verified live - scaling this consumer 1->4 replicas under
        // repeated-same-hash load made throughput go *down* (row-lock contention on the read-then-
        // write pattern), not up. ExecuteUpdateAsync fixes both the race and the extra round trip.
        var updated = await context.Links
            .Where(x => x.Hash == @event.Hash)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ClickCount, x => x.ClickCount + 1), cancellationToken);

        if (updated == 0)
        {
            // LinkCreatedEvent for this hash may not have been consumed yet (ordering across
            // queues isn't guaranteed), or the hash is simply unknown. Either way there's no row
            // to increment - log and move on rather than failing/requeueing forever.
            _logger.LogWarning("No link found for hash {Hash}, click count not incremented", @event.Hash);
            return;
        }

        _logger.LogInformation("Incremented click count for hash {Hash}", @event.Hash);
    }
}
