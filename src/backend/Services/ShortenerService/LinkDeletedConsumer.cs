using Common;
using Common.Models;
using Contracts.Events;
using Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ShortenerService;

// Consumes batches of LinkDeletedEvent (manual delete only - the TTL sweep job never publishes
// this, see LinkExpiryWorker's own comment for why) and marks the matching row deleted.
// Idempotent: a hash whose DeletedAt is already set is a redelivery, or the TTL sweep got there
// first - either way, a silent no-op, not an error.
public sealed class LinkDeletedConsumer(
    IMessageConsumer consumer,
    IDbContextFactory<DatabaseContext> dbContextFactory,
    ILinkDeletionMarkerStore deletionMarkers,
    IEntityCacheService<Link> cache,
    ILogger<LinkDeletedConsumer> logger) : BackgroundService
{
    private const string QueueName = "shortener-service.link-deleted";

    private readonly IMessageConsumer _consumer = consumer;
    private readonly IDbContextFactory<DatabaseContext> _dbContextFactory = dbContextFactory;
    private readonly ILinkDeletionMarkerStore _deletionMarkers = deletionMarkers;
    private readonly IEntityCacheService<Link> _cache = cache;
    private readonly ILogger<LinkDeletedConsumer> _logger = logger;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        _consumer.ConsumeBatchAsync<LinkDeletedEvent>(QueueName, Topics.LinkDeleted, HandleBatchAsync, stoppingToken);

    internal async Task<BatchOutcome> HandleBatchAsync(
        IReadOnlyList<BatchItem<LinkDeletedEvent>> batch, CancellationToken cancellationToken)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var hashes = batch.Select(x => x.Payload.Hash).Distinct().ToList();
        var existingHashes = await context.Links
            .Where(x => hashes.Contains(x.Hash))
            .Select(x => x.Hash)
            .ToHashSetAsync(cancellationToken);

        var now = DateTime.UtcNow;

        // Only rows not already deleted get touched - ExecuteUpdateAsync (the same bulk-update
        // pattern ClickCountFlushWorker uses for ClickCount) rather than loading tracked entities,
        // since Link.DeletedAt is an init-only record property EF can update this way without the
        // ceremony of a per-entity ChangeTracker.Entry(...).Property(...) call.
        if (existingHashes.Count > 0)
        {
            await context.Links
                .Where(x => hashes.Contains(x.Hash) && x.DeletedAt == null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.DeletedAt, now), cancellationToken);
        }

        // Confirm every hash that exists, not just the ones this call just updated - a hash that
        // was already deleted before this tick (redelivery, or the TTL sweep beat us to it) still
        // deserves a confirmed tombstone if, for whatever reason, one was never written (e.g. an
        // earlier confirm attempt hit a transient Redis failure). Using `now` as the tombstone's
        // DeletedAt even for an already-deleted row is an approximation, not the row's true original
        // timestamp - acceptable here since the tombstone's timestamp is informational (shown on the
        // "gone" page), not load-bearing for any correctness decision.
        //
        // Also invalidates the main link:{hash} cache-aside entry (IEntityCacheService<Link>, a
        // separate Redis key/path from the tombstone above) - found in review: without this, a Link
        // cached before deletion kept showing DeletedAt == null forever, with correctness resting
        // entirely on the tombstone write never failing. Belt and suspenders: if the tombstone
        // confirm below fails but this invalidation succeeds (or vice versa), RedirectApi's live
        // DeletedAt check on the next Postgres-backed cache miss still catches it.
        foreach (var hash in existingHashes)
        {
            await _deletionMarkers.ConfirmAsync(hash, DeletedReason.Manual, now, cancellationToken);
            await _cache.InvalidateCacheAsync(hash);
        }

        // Unlike ClickCountFlushWorker's identical-shaped "no row found" case (which just logs and
        // moves on - a missed click-count increment is an approximate display number by design, not
        // worth retrying), a missing link here gets a real retry instead of being silently dropped:
        // LinkApi's own optimistic cache write means a delete request can be accepted (and this event
        // published) for a link whose LinkCreatedEvent hasn't been consumed yet - RabbitMQ gives no
        // cross-topic ordering guarantee, and the SQLite fallback queue can delay create further
        // still. Throwing hands this to RabbitMqConsumer's existing bounded delivery-attempt retry
        // (with backoff) - the create almost certainly lands before attempts run out. Silently
        // dropping it instead would mean the link stays alive forever despite its owner explicitly
        // asking for it to be deleted, with nobody ever finding out.
        var missingHashes = hashes.Where(h => !existingHashes.Contains(h)).ToList();
        if (missingHashes.Count > 0)
        {
            throw new InvalidOperationException(
                $"No link found for hash(es) {string.Join(", ", missingHashes)} - the create event may " +
                "not have been processed yet. Retrying via the consumer's redelivery mechanism.");
        }

        _logger.LogInformation("Marked {Count} link(s) deleted from this batch", existingHashes.Count);
        return BatchOutcome.Success;
    }
}
