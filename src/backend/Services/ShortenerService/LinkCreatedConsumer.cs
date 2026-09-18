using Common.Models;
using Contracts.Events;
using Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ShortenerService;

// Consumes LinkCreatedEvent (LinkApi already generated the hash) and persists it to Postgres Links.
// Redelivery-safe: a message whose hash is already stored is treated as already processed and skipped,
// rather than failing on the unique-key violation.
public sealed class LinkCreatedConsumer(
    IMessageConsumer consumer,
    IDbContextFactory<DatabaseContext> dbContextFactory,
    ILogger<LinkCreatedConsumer> logger) : BackgroundService
{
    private const string QueueName = "shortener-service.link-created";

    private readonly IMessageConsumer _consumer = consumer;
    private readonly IDbContextFactory<DatabaseContext> _dbContextFactory = dbContextFactory;
    private readonly ILogger<LinkCreatedConsumer> _logger = logger;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        _consumer.ConsumeAsync<LinkCreatedEvent>(QueueName, Topics.LinkCreated, HandleAsync, stoppingToken);

    private async Task HandleAsync(LinkCreatedEvent @event, CancellationToken cancellationToken)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var alreadyStored = await context.Links.AnyAsync(x => x.Hash == @event.Hash, cancellationToken);
        if (alreadyStored)
        {
            _logger.LogInformation("Link {Hash} is already persisted, skipping redelivered message", @event.Hash);
            return;
        }

        context.Links.Add(new Link(@event.Hash, @event.OriginalLink, @event.ShortenLink, @event.CreatedAt, @event.UserId));
        await context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Persisted link {Hash}", @event.Hash);
    }
}
