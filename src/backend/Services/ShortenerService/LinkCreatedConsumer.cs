using Common;
using Common.Models;
using Contracts.Events;
using Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ShortenerService;

// Consumes batches of LinkCreatedEvent (LinkApi already generated the hash) and persists them to
// Postgres Links. Redelivery-safe: a hash already stored is treated as already processed and
// skipped, rather than failing the whole batch on a unique-key violation.
public sealed class LinkCreatedConsumer(
    IMessageConsumer consumer,
    IDbContextFactory<DatabaseContext> dbContextFactory,
    IEntityCacheService<Link> cache,
    ILogger<LinkCreatedConsumer> logger) : BackgroundService
{
    private const string QueueName = "shortener-service.link-created";

    private readonly IMessageConsumer _consumer = consumer;
    private readonly IDbContextFactory<DatabaseContext> _dbContextFactory = dbContextFactory;
    private readonly IEntityCacheService<Link> _cache = cache;
    private readonly ILogger<LinkCreatedConsumer> _logger = logger;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        _consumer.ConsumeBatchAsync<LinkCreatedEvent>(QueueName, Topics.LinkCreated, HandleBatchAsync, stoppingToken);

    internal async Task<BatchOutcome> HandleBatchAsync(
        IReadOnlyList<BatchItem<LinkCreatedEvent>> batch, CancellationToken cancellationToken)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        // Same hash can appear twice in one batch if a redelivery lands alongside a fresher copy of
        // itself - keep the last occurrence.
        var distinctByHash = batch
            .Select(x => x.Payload)
            .GroupBy(x => x.Hash)
            .Select(g => g.Last())
            .ToList();

        var hashes = distinctByHash.Select(x => x.Hash).ToList();
        var alreadyStored = await context.Links
            .Where(x => hashes.Contains(x.Hash))
            .Select(x => x.Hash)
            .ToHashSetAsync(cancellationToken);

        var newLinks = distinctByHash.Where(x => !alreadyStored.Contains(x.Hash)).ToList();
        var newLinkEntities = newLinks.Select(e => new Link(e.Hash, e.OriginalLink, e.ShortenLink, e.CreatedAt, e.UserId)).ToList();
        context.Links.AddRange(newLinkEntities);
        await context.SaveChangesAsync(cancellationToken);

        // Pre-warms Redis right after the row genuinely exists in Postgres - without this,
        // RedirectApi's cache only ever gets populated lazily on its own miss, so a client that
        // follows a just-created short link before this consumer caught up would hit both a Redis
        // miss AND a Postgres miss (the row wasn't here yet either), getting a false 404 for a hash
        // LinkApi already confirmed as created.
        foreach (var link in newLinkEntities)
        {
            await _cache.CacheAsync(link, link.Hash, cancellationToken);
        }

        if (alreadyStored.Count > 0)
        {
            _logger.LogInformation(
                "{Count} link(s) in this batch were already persisted, skipping their inserts", alreadyStored.Count);
        }

        _logger.LogInformation("Persisted {Count} new link(s) from this batch", newLinks.Count);
        return BatchOutcome.Success;
    }
}
