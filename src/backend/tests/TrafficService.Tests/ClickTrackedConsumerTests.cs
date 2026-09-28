using Common;
using Common.Models;
using Contracts.Events;
using Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TrafficService;

namespace TrafficService.Tests;

public class ClickTrackedConsumerTests
{
    private static IDbContextFactory<DatabaseContext> NewFactory(string dbName)
    {
        var options = new DbContextOptionsBuilder<DatabaseContext>().UseInMemoryDatabase(dbName).Options;
        var factory = new Mock<IDbContextFactory<DatabaseContext>>();
        factory.Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new DatabaseContext(options));
        return factory.Object;
    }

    private static ClickTrackedEvent NewEvent(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Hash = "abc12345",
        InboundLink = "https://short.example/abc12345",
        OutboundLink = "https://example.com",
        ClickedAt = DateTime.UtcNow,
        UserAgent = "",
        Referrer = "",
    };

    private static List<BatchItem<ClickTrackedEvent>> NewBatch(params ClickTrackedEvent[] events) =>
        events.Select(e => new BatchItem<ClickTrackedEvent>(Guid.NewGuid().ToString(), e)).ToList();

    private static ClickTrackedConsumer NewSut(IDbContextFactory<DatabaseContext> factory) =>
        new(
            Mock.Of<IMessageConsumer>(),
            factory,
            Mock.Of<IClickMetaStore>(),
            Mock.Of<IUserAgentParser>(),
            Mock.Of<IGeoIpResolver>(),
            NullLogger<ClickTrackedConsumer>.Instance);

    [Fact]
    public async Task HandleBatchAsync_NewClick_PersistsToDatabase()
    {
        var factory = NewFactory(Guid.NewGuid().ToString());
        var sut = NewSut(factory);
        var @event = NewEvent();

        await sut.HandleBatchAsync(NewBatch(@event), CancellationToken.None);

        await using var verify = await factory.CreateDbContextAsync();
        var stored = await verify.Clicks.SingleAsync();
        Assert.Equal(@event.Id, stored.Id);
        Assert.Equal("abc12345", stored.Hash);
    }

    [Fact]
    public async Task HandleBatchAsync_DuplicateIdWithinOneBatch_PersistsOnce()
    {
        // Two deliveries for the same click Id can land in the same batch (e.g. a requeue racing a
        // fresh delivery) - must dedupe within the batch, not just across separate batches.
        var factory = NewFactory(Guid.NewGuid().ToString());
        var sut = NewSut(factory);
        var @event = NewEvent();

        await sut.HandleBatchAsync(NewBatch(@event, @event with { }), CancellationToken.None);

        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(1, await verify.Clicks.CountAsync());
    }

    [Fact]
    public async Task HandleBatchAsync_RedeliveredInALaterBatch_IsSkippedWithoutError()
    {
        var factory = NewFactory(Guid.NewGuid().ToString());
        var sut = NewSut(factory);
        var @event = NewEvent();
        await sut.HandleBatchAsync(NewBatch(@event), CancellationToken.None);

        // Same Id arrives again in an entirely separate batch (e.g. after a broker requeue) - must
        // not throw a primary-key violation and must not duplicate the row.
        await sut.HandleBatchAsync(NewBatch(@event), CancellationToken.None);

        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(1, await verify.Clicks.CountAsync());
    }

    [Fact]
    public async Task HandleBatchAsync_RedeliveredAfterMongoFailure_RetriesMongoWriteInsteadOfSkippingIt()
    {
        // Regression: the two writes aren't transactional. If SaveManyAsync throws (transient Mongo
        // outage) after the Postgres rows already committed, a naive "alreadyStored -> skip
        // everything" redelivery guard would permanently lose that click's ClickMeta document, since
        // Postgres already has the row and would short-circuit every future retry too.
        var factory = NewFactory(Guid.NewGuid().ToString());
        var clickMetaStore = new Mock<IClickMetaStore>();
        clickMetaStore.SetupSequence(x => x.SaveManyAsync(It.IsAny<IReadOnlyCollection<ClickMeta>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("mongo unreachable"))
            .Returns(Task.CompletedTask);
        var sut = new ClickTrackedConsumer(
            Mock.Of<IMessageConsumer>(),
            factory,
            clickMetaStore.Object,
            Mock.Of<IUserAgentParser>(),
            Mock.Of<IGeoIpResolver>(),
            NullLogger<ClickTrackedConsumer>.Instance);
        var @event = NewEvent();
        var batch = NewBatch(@event);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.HandleBatchAsync(batch, CancellationToken.None));
        // Redelivery: same batch content, same event id - the Postgres row already committed from the
        // first (failed) attempt.
        await sut.HandleBatchAsync(batch, CancellationToken.None);

        clickMetaStore.Verify(
            x => x.SaveManyAsync(It.IsAny<IReadOnlyCollection<ClickMeta>>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(1, await verify.Clicks.CountAsync());
    }

    [Fact]
    public async Task HandleBatchAsync_DifferentIds_PersistsBoth()
    {
        var factory = NewFactory(Guid.NewGuid().ToString());
        var sut = NewSut(factory);

        await sut.HandleBatchAsync(NewBatch(NewEvent(), NewEvent()), CancellationToken.None);

        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(2, await verify.Clicks.CountAsync());
    }
}
