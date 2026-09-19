using Common.Models;
using Contracts.Events;
using Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace TrafficService;

// Consumes ClickTrackedEvent and persists it to Postgres Clicks. Redelivery-safe: the event
// carries its own Id (generated once by RedirectApi), so a message whose Id is already stored is
// treated as already processed and skipped, rather than failing on the primary-key violation.
public sealed class ClickTrackedConsumer(
    IMessageConsumer consumer,
    IDbContextFactory<DatabaseContext> dbContextFactory,
    ILogger<ClickTrackedConsumer> logger) : BackgroundService
{
    private const string QueueName = "traffic-service.click-tracked";

    private readonly IMessageConsumer _consumer = consumer;
    private readonly IDbContextFactory<DatabaseContext> _dbContextFactory = dbContextFactory;
    private readonly ILogger<ClickTrackedConsumer> _logger = logger;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        _consumer.ConsumeAsync<ClickTrackedEvent>(QueueName, Topics.ClickTracked, HandleAsync, stoppingToken);

    internal async Task HandleAsync(ClickTrackedEvent @event, CancellationToken cancellationToken)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var alreadyStored = await context.Clicks.AnyAsync(x => x.Id == @event.Id, cancellationToken);
        if (alreadyStored)
        {
            _logger.LogInformation("Click {ClickId} is already persisted, skipping redelivered message", @event.Id);
            return;
        }

        context.Clicks.Add(new Click(@event.Id, @event.ClickedAt, @event.InboundLink, @event.OutboundLink, @event.Hash));
        await context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Persisted click {ClickId} for hash {Hash}", @event.Id, @event.Hash);
    }
}
