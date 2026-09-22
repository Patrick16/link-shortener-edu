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

        var link = await context.Links.FirstOrDefaultAsync(x => x.Hash == @event.Hash, cancellationToken);
        if (link is null)
        {
            // LinkCreatedEvent for this hash may not have been consumed yet (ordering across
            // queues isn't guaranteed), or the hash is simply unknown. Either way there's no row
            // to increment - log and move on rather than failing/requeueing forever.
            _logger.LogWarning("No link found for hash {Hash}, click count not incremented", @event.Hash);
            return;
        }

        // Link is an immutable record everywhere else in the codebase; going through the change
        // tracker's property accessor (instead of ExecuteUpdateAsync's single atomic SQL UPDATE)
        // keeps this consistent with it and with the InMemory provider the tests use. A single
        // worker instance in this project's docker-compose, so the read-then-write race that
        // costs is negligible in practice.
        context.Entry(link).Property(x => x.ClickCount).CurrentValue = link.ClickCount + 1;
        await context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Incremented click count for hash {Hash}", @event.Hash);
    }
}
