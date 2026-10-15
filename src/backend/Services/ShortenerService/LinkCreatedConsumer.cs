using Common;
using Common.Models;
using Contracts.Events;
using Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ShortenerService;

// Consumes batches of LinkCreatedEvent (LinkApi already generated the hash, or it's a user-chosen
// alias - see LinkCreatedEvent.HasCustomAlias) and persists them to Postgres Links.
// Redelivery-safe: a hash already stored with the SAME OriginalLink is treated as already
// processed and skipped, rather than failing the whole batch on a unique-key violation. A hash
// already stored with a DIFFERENT OriginalLink is a genuine conflict (most commonly: a custom
// alias that lost a uniqueness race), not a redelivery - that message is reported as poison
// instead of being silently dropped, since silently dropping it would mean the caller who was
// already told this hash/alias is theirs never finds out it isn't.
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

        var poisonMessages = new List<PoisonMessage>();

        // Same hash can appear more than once in one batch - either genuine redeliveries of the
        // identical message (same OriginalLink - keep the last, drop the rest silently, same as
        // before this fix), or, now that a hash can be a human-chosen alias, two genuinely
        // different links that both landed in the same batch window claiming the same hash. The
        // latter case used to vanish untraced (g.Last() silently dropping every earlier item in
        // the group, poison or not) - now every dropped item is compared against the survivor
        // that actually proceeds, and a content mismatch is reported as poison instead of lost.
        var distinctByHash = new List<BatchItem<LinkCreatedEvent>>();
        foreach (var group in batch.GroupBy(x => x.Payload.Hash))
        {
            var groupItems = group.ToList();
            var survivor = groupItems[^1];
            distinctByHash.Add(survivor);

            foreach (var loser in groupItems.SkipLast(1))
            {
                if (loser.Payload.OriginalLink == survivor.Payload.OriginalLink)
                {
                    continue; // identical content - an intra-batch redelivery, not a conflict
                }

                poisonMessages.Add(new PoisonMessage(loser.MessageId, ConflictReason(loser.Payload.Hash, survivor.Payload.HasCustomAlias)));
            }
        }

        var hashes = distinctByHash.Select(x => x.Payload.Hash).ToList();

        // For the synchronous gRPC path (batch-of-one, see MessagingGrpcService), this pre-check
        // almost always reports "not yet stored" even for a genuine race - two concurrent callers
        // both land here before either has committed. The real conflict resolution for that case
        // happens in the DbUpdateException recovery below; this pass still matters for the
        // RabbitMQ path, where a redelivery landing in a later batch than its original IS already
        // stored by the time this runs.
        var existingLinks = await context.Links
            .Where(x => hashes.Contains(x.Hash))
            .ToDictionaryAsync(x => x.Hash, cancellationToken);

        var newItems = ClassifyAgainstExisting(distinctByHash, existingLinks, poisonMessages);
        var newLinkEntities = newItems.Select(BuildEntity).ToList();
        context.Links.AddRange(newLinkEntities);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // This is where a sync-gRPC batch-of-one race actually resolves (see the pre-check's
            // own comment). A second replica of this same consumer (competing consumers on one
            // queue - shortener-service can be scaled to N instances) can hit the identical race:
            // both see a hash as "not yet stored" above and both try to insert it, one wins, the
            // other hits this unique violation. By the time we're here, Postgres has already
            // committed the winner's row (that's the only way our insert could have failed with a
            // conflict instead of blocking) - so the re-query below deterministically sees it, no
            // further race on the read itself. Without this catch, the whole batch would propagate
            // up to RabbitMqConsumer.HandleBatchFailureAsync and nack-with-requeue every other,
            // unrelated message in it too, just to retry one real conflict. Detaching and
            // re-checking against Postgres keeps the race local to the one hash it actually
            // affects.
            foreach (var entry in context.ChangeTracker.Entries<Link>().ToList())
            {
                entry.State = EntityState.Detached;
            }

            var nowExisting = await context.Links
                .Where(x => hashes.Contains(x.Hash))
                .ToDictionaryAsync(x => x.Hash, cancellationToken);

            newItems = ClassifyAgainstExisting(newItems, nowExisting, poisonMessages);
            newLinkEntities = newItems.Select(BuildEntity).ToList();

            context.Links.AddRange(newLinkEntities);
            await context.SaveChangesAsync(cancellationToken);
        }

        // Pre-warms Redis right after the row genuinely exists in Postgres - without this,
        // RedirectApi's cache only ever gets populated lazily on its own miss, so a client that
        // follows a just-created short link before this consumer caught up would hit both a Redis
        // miss AND a Postgres miss (the row wasn't here yet either), getting a false 404 for a hash
        // LinkApi already confirmed as created.
        foreach (var link in newLinkEntities)
        {
            await _cache.CacheAsync(link, link.Hash, cancellationToken);
        }

        var skippedCount = distinctByHash.Count - newItems.Count - poisonMessages.Count;
        if (skippedCount > 0)
        {
            _logger.LogInformation(
                "{Count} link(s) in this batch were already persisted, skipping their inserts", skippedCount);
        }

        if (poisonMessages.Count > 0)
        {
            _logger.LogWarning(
                "{Count} link(s) in this batch conflicted with an existing, different link: {Reasons}",
                poisonMessages.Count,
                string.Join("; ", poisonMessages.Select(p => p.Reason)));
        }

        _logger.LogInformation("Persisted {Count} new link(s) from this batch", newItems.Count);
        return poisonMessages.Count > 0 ? BatchOutcome.WithPoison(poisonMessages) : BatchOutcome.Success;
    }

    // Splits a batch against what's currently in Postgres for the same hashes: a hash with no
    // existing row is new (returned); a hash whose existing row has the SAME OriginalLink is a
    // true duplicate/redelivery (silently dropped, as before this fix); a hash whose existing row
    // has a DIFFERENT OriginalLink is a genuine conflict (appended to `poisonMessages`, mutated in
    // place since this runs twice per call - once pre-insert, once after a unique-key conflict).
    private static List<BatchItem<LinkCreatedEvent>> ClassifyAgainstExisting(
        IReadOnlyList<BatchItem<LinkCreatedEvent>> items,
        IReadOnlyDictionary<string, Link> existingByHash,
        List<PoisonMessage> poisonMessages)
    {
        var stillNew = new List<BatchItem<LinkCreatedEvent>>();
        foreach (var item in items)
        {
            if (!existingByHash.TryGetValue(item.Payload.Hash, out var existing))
            {
                stillNew.Add(item);
                continue;
            }

            if (existing.OriginalLink == item.Payload.OriginalLink)
            {
                continue; // same link, already persisted - redelivery or a losing racing replica, not an error
            }

            poisonMessages.Add(new PoisonMessage(item.MessageId, ConflictReason(item.Payload.Hash, existing.HasCustomAlias)));
        }

        return stillNew;
    }

    // Takes the winning side's HasCustomAlias directly (not a Link) so this can serve both callers:
    // ClassifyAgainstExisting has a persisted winning row to read it from, but the intra-batch dedup
    // loop above only has the surviving BatchItem - neither side is a Link yet at that point.
    private static string ConflictReason(string hash, bool hasCustomAlias) =>
        hasCustomAlias
            ? $"Alias '{hash}' is already in use by a different link."
            : $"Hash '{hash}' collided with an existing link - unexpected for a generated hash, investigate.";

    private static Link BuildEntity(BatchItem<LinkCreatedEvent> item) => new(
        item.Payload.Hash,
        item.Payload.OriginalLink,
        item.Payload.ShortenLink,
        item.Payload.CreatedAt,
        item.Payload.UserId,
        HasCustomAlias: item.Payload.HasCustomAlias);
}
