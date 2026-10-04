using Common;
using Common.Models;
using Contracts.Events;
using Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ShortenerService;

namespace ShortenerService.Tests;

public class LinkCreatedConsumerTests
{
    private static IDbContextFactory<DatabaseContext> NewFactory(string dbName)
    {
        var options = new DbContextOptionsBuilder<DatabaseContext>().UseInMemoryDatabase(dbName).Options;
        var factory = new Mock<IDbContextFactory<DatabaseContext>>();
        factory.Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new DatabaseContext(options));
        return factory.Object;
    }

    private static LinkCreatedEvent NewEvent(string hash = "abc12345") => new()
    {
        Hash = hash,
        OriginalLink = "https://example.com",
        ShortenLink = hash,
        CreatedAt = DateTime.UtcNow,
        UserId = null,
    };

    private static List<BatchItem<LinkCreatedEvent>> NewBatch(params LinkCreatedEvent[] events) =>
        events.Select(e => new BatchItem<LinkCreatedEvent>(Guid.NewGuid().ToString(), e)).ToList();

    private static LinkCreatedConsumer NewSut(IDbContextFactory<DatabaseContext> factory) =>
        new(Mock.Of<IMessageConsumer>(), factory, Mock.Of<IEntityCacheService<Link>>(), NullLogger<LinkCreatedConsumer>.Instance);

    [Fact]
    public async Task HandleBatchAsync_NewLink_PersistsToDatabase()
    {
        var factory = NewFactory(Guid.NewGuid().ToString());
        var sut = NewSut(factory);

        await sut.HandleBatchAsync(NewBatch(NewEvent()), CancellationToken.None);

        await using var verify = await factory.CreateDbContextAsync();
        var stored = await verify.Links.SingleAsync();
        Assert.Equal("abc12345", stored.Hash);
        Assert.Equal("https://example.com", stored.OriginalLink);
    }

    [Fact]
    public async Task HandleBatchAsync_DuplicateHashWithinOneBatch_PersistsOnce()
    {
        var factory = NewFactory(Guid.NewGuid().ToString());
        var sut = NewSut(factory);
        var @event = NewEvent();

        await sut.HandleBatchAsync(NewBatch(@event, @event with { }), CancellationToken.None);

        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(1, await verify.Links.CountAsync());
    }

    [Fact]
    public async Task HandleBatchAsync_RedeliveredInALaterBatch_IsSkippedWithoutError()
    {
        var factory = NewFactory(Guid.NewGuid().ToString());
        var sut = NewSut(factory);
        var @event = NewEvent();
        await sut.HandleBatchAsync(NewBatch(@event), CancellationToken.None);

        // Same hash arrives again in a separate batch (e.g. after a broker requeue) - must not throw
        // a unique-key violation and must not duplicate the row.
        await sut.HandleBatchAsync(NewBatch(@event), CancellationToken.None);

        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(1, await verify.Links.CountAsync());
    }

    [Fact]
    public async Task HandleBatchAsync_NewLink_PreWarmsCache()
    {
        // Regression: without this, RedirectApi's Redis cache was only ever populated lazily on its
        // own miss - a client following a just-created short link before this consumer caught up
        // could hit both a Redis miss AND a Postgres miss (the row wasn't persisted yet either),
        // getting a false 404 for a hash LinkApi already confirmed as created.
        var factory = NewFactory(Guid.NewGuid().ToString());
        var cache = new Mock<IEntityCacheService<Link>>();
        var sut = new LinkCreatedConsumer(Mock.Of<IMessageConsumer>(), factory, cache.Object, NullLogger<LinkCreatedConsumer>.Instance);
        var @event = NewEvent();

        await sut.HandleBatchAsync(NewBatch(@event), CancellationToken.None);

        cache.Verify(
            x => x.CacheAsync(
                It.Is<Link>(l => l.Hash == @event.Hash && l.OriginalLink == @event.OriginalLink),
                @event.Hash,
                It.IsAny<CancellationToken>(),
                3600),
            Times.Once);
    }

    [Fact]
    public async Task HandleBatchAsync_AlreadyStoredLink_DoesNotReCacheIt()
    {
        var factory = NewFactory(Guid.NewGuid().ToString());
        var cache = new Mock<IEntityCacheService<Link>>();
        var sut = new LinkCreatedConsumer(Mock.Of<IMessageConsumer>(), factory, cache.Object, NullLogger<LinkCreatedConsumer>.Instance);
        var @event = NewEvent();
        await sut.HandleBatchAsync(NewBatch(@event), CancellationToken.None);
        cache.Invocations.Clear();

        // Redelivery of an already-persisted hash - must not re-warm the cache a second time.
        await sut.HandleBatchAsync(NewBatch(@event), CancellationToken.None);

        cache.Verify(
            x => x.CacheAsync(It.IsAny<Link>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<int>()),
            Times.Never);
    }

    [Fact]
    public async Task HandleBatchAsync_DifferentHashes_PersistsBoth()
    {
        var factory = NewFactory(Guid.NewGuid().ToString());
        var sut = NewSut(factory);

        await sut.HandleBatchAsync(NewBatch(NewEvent("hash0001"), NewEvent("hash0002")), CancellationToken.None);

        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(2, await verify.Links.CountAsync());
    }
}
