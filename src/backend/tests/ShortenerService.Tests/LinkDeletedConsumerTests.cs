using Common;
using Common.Models;
using Contracts.Events;
using Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ShortenerService;

namespace ShortenerService.Tests;

// SQLite in-memory, not EF Core's InMemory provider - same reasoning as ClickTrackedConsumerTests:
// HandleBatchAsync uses ExecuteUpdateAsync, which the InMemory provider doesn't support at all.
public sealed class LinkDeletedConsumerTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public LinkDeletedConsumerTests() => _connection.Open();

    public void Dispose() => _connection.Dispose();

    private IDbContextFactory<DatabaseContext> NewFactory()
    {
        var options = new DbContextOptionsBuilder<DatabaseContext>().UseSqlite(_connection).Options;
        using (var init = new DatabaseContext(options))
        {
            init.Database.EnsureCreated();
        }

        var factory = new Mock<IDbContextFactory<DatabaseContext>>();
        factory.Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new DatabaseContext(options));
        return factory.Object;
    }

    private static Link NewLink(string hash = "abc12345") =>
        new(hash, "https://example.com", hash, DateTime.UtcNow, null);

    private static List<BatchItem<LinkDeletedEvent>> NewBatch(params string[] hashes) =>
        hashes.Select(h => new BatchItem<LinkDeletedEvent>(Guid.NewGuid().ToString(), new LinkDeletedEvent { Hash = h })).ToList();

    private static LinkDeletedConsumer NewSut(IDbContextFactory<DatabaseContext> factory, out Mock<ILinkDeletionMarkerStore> deletionMarkers) =>
        NewSut(factory, out deletionMarkers, out _);

    private static LinkDeletedConsumer NewSut(
        IDbContextFactory<DatabaseContext> factory,
        out Mock<ILinkDeletionMarkerStore> deletionMarkers,
        out Mock<IEntityCacheService<Link>> cache)
    {
        deletionMarkers = new Mock<ILinkDeletionMarkerStore>();
        cache = new Mock<IEntityCacheService<Link>>();
        return new LinkDeletedConsumer(Mock.Of<IMessageConsumer>(), factory, deletionMarkers.Object, cache.Object, NullLogger<LinkDeletedConsumer>.Instance);
    }

    [Fact]
    public async Task HandleBatchAsync_ExistingLink_InvalidatesTheMainCacheEntry()
    {
        // Found in review: without this, a Link cached before deletion kept showing DeletedAt ==
        // null forever, with correctness resting entirely on the separate tombstone write never
        // failing. This invalidation is independent of that tombstone confirm (below) succeeding.
        var factory = NewFactory();
        await using (var seed = await factory.CreateDbContextAsync())
        {
            seed.Links.Add(NewLink());
            await seed.SaveChangesAsync();
        }
        var sut = NewSut(factory, out _, out var cache);

        await sut.HandleBatchAsync(NewBatch("abc12345"), CancellationToken.None);

        cache.Verify(x => x.InvalidateCacheAsync("abc12345"), Times.Once);
    }

    [Fact]
    public async Task HandleBatchAsync_ExistingLink_SetsDeletedAtAndConfirmsTombstone()
    {
        var factory = NewFactory();
        await using (var seed = await factory.CreateDbContextAsync())
        {
            seed.Links.Add(NewLink());
            await seed.SaveChangesAsync();
        }
        var sut = NewSut(factory, out var deletionMarkers);

        var outcome = await sut.HandleBatchAsync(NewBatch("abc12345"), CancellationToken.None);

        Assert.Equal(BatchOutcome.Success, outcome);
        await using var verify = await factory.CreateDbContextAsync();
        var stored = await verify.Links.SingleAsync();
        Assert.NotNull(stored.DeletedAt);
        deletionMarkers.Verify(
            x => x.ConfirmAsync("abc12345", DeletedReason.Manual, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task HandleBatchAsync_AlreadyDeletedLink_IsIdempotentNoOp()
    {
        var factory = NewFactory();
        var deletedAt = DateTime.UtcNow.AddMinutes(-5);
        await using (var seed = await factory.CreateDbContextAsync())
        {
            seed.Links.Add(NewLink() with { DeletedAt = deletedAt });
            await seed.SaveChangesAsync();
        }
        var sut = NewSut(factory, out var deletionMarkers);

        var outcome = await sut.HandleBatchAsync(NewBatch("abc12345"), CancellationToken.None);

        Assert.Equal(BatchOutcome.Success, outcome);
        await using var verify = await factory.CreateDbContextAsync();
        var stored = await verify.Links.SingleAsync();
        // Untouched - a redelivery/race with the sweep job must not overwrite the original DeletedAt.
        // SQLite round-trips DateTime losslessly, unlike the real Postgres provider's `timestamptz`
        // precision - fine here since this test only cares that the value is unchanged, not exact.
        Assert.Equal(deletedAt, stored.DeletedAt);
        // Still confirms the tombstone - see HandleBatchAsync's own comment on why this is safe and
        // intentional (covers a prior confirm attempt that failed open on a Redis hiccup).
        deletionMarkers.Verify(
            x => x.ConfirmAsync("abc12345", DeletedReason.Manual, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task HandleBatchAsync_HashDoesNotExistYet_ThrowsWithoutConfirmingATombstone()
    {
        // The delete-before-create race: LinkApi's optimistic cache write means a DELETE request
        // can be accepted (and this event published) before LinkCreatedConsumer has actually
        // persisted the row - RabbitMQ gives no cross-topic ordering guarantee. Must throw (so
        // RabbitMqConsumer retries it) rather than silently drop it.
        var factory = NewFactory();
        var sut = NewSut(factory, out var deletionMarkers);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.HandleBatchAsync(NewBatch("never-created"), CancellationToken.None));

        deletionMarkers.Verify(
            x => x.ConfirmAsync(It.IsAny<string>(), It.IsAny<DeletedReason>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task HandleBatchAsync_MixedBatchWithOneMissingHash_PersistsTheFoundOnesBeforeThrowing()
    {
        // The found hashes in the batch must not be lost just because a different, unrelated hash
        // in the same batch hasn't been created yet - RabbitMqConsumer's retry will re-deliver the
        // whole batch, but by then the already-processed hashes are idempotent no-ops (see the
        // AlreadyDeletedLink test above), not a second chance to lose them.
        var factory = NewFactory();
        await using (var seed = await factory.CreateDbContextAsync())
        {
            seed.Links.Add(NewLink("found0001"));
            await seed.SaveChangesAsync();
        }
        var sut = NewSut(factory, out var deletionMarkers);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.HandleBatchAsync(NewBatch("found0001", "missing01"), CancellationToken.None));

        await using var verify = await factory.CreateDbContextAsync();
        var found = await verify.Links.SingleAsync(x => x.Hash == "found0001");
        Assert.NotNull(found.DeletedAt);
        deletionMarkers.Verify(
            x => x.ConfirmAsync("found0001", DeletedReason.Manual, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task HandleBatchAsync_DeleteRetriedAfterTheLinkIsFinallyCreated_ThenSucceeds()
    {
        // End-to-end shape of the delete-before-create race's resolution: delete arrives first and
        // throws (simulating RabbitMqConsumer's retry backoff), the create catches up, and the
        // retried delete then succeeds - mirrors LinkCreatedConsumerTests' own race-resolution style
        // of test, but as a deterministic sequence rather than genuine thread overlap, since this is
        // about retry ordering, not two DbContexts racing the same statement.
        var factory = NewFactory();
        var deleteSut = NewSut(factory, out var deletionMarkers);
        var createSut = new LinkCreatedConsumer(Mock.Of<IMessageConsumer>(), factory, Mock.Of<IEntityCacheService<Link>>(), NullLogger<LinkCreatedConsumer>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => deleteSut.HandleBatchAsync(NewBatch("abc12345"), CancellationToken.None));

        await createSut.HandleBatchAsync(
            [new BatchItem<LinkCreatedEvent>(Guid.NewGuid().ToString(), new LinkCreatedEvent
            {
                Hash = "abc12345",
                OriginalLink = "https://example.com",
                ShortenLink = "abc12345",
                CreatedAt = DateTime.UtcNow,
            })],
            CancellationToken.None);

        var outcome = await deleteSut.HandleBatchAsync(NewBatch("abc12345"), CancellationToken.None);

        Assert.Equal(BatchOutcome.Success, outcome);
        await using var verify = await factory.CreateDbContextAsync();
        var stored = await verify.Links.SingleAsync();
        Assert.NotNull(stored.DeletedAt);
        deletionMarkers.Verify(
            x => x.ConfirmAsync("abc12345", DeletedReason.Manual, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
