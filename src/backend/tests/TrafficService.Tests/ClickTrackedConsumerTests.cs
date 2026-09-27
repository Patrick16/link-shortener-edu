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

    private static ClickTrackedConsumer NewSut(IDbContextFactory<DatabaseContext> factory) =>
        new(
            Mock.Of<IMessageConsumer>(),
            factory,
            Mock.Of<IClickMetaStore>(),
            Mock.Of<IUserAgentParser>(),
            Mock.Of<IGeoIpResolver>(),
            NullLogger<ClickTrackedConsumer>.Instance);

    [Fact]
    public async Task HandleAsync_NewClick_PersistsToDatabase()
    {
        var factory = NewFactory(Guid.NewGuid().ToString());
        var sut = NewSut(factory);
        var @event = NewEvent();

        await sut.HandleAsync(@event, CancellationToken.None);

        await using var verify = await factory.CreateDbContextAsync();
        var stored = await verify.Clicks.SingleAsync();
        Assert.Equal(@event.Id, stored.Id);
        Assert.Equal("abc12345", stored.Hash);
    }

    [Fact]
    public async Task HandleAsync_RedeliveredMessage_IsSkippedWithoutError()
    {
        var factory = NewFactory(Guid.NewGuid().ToString());
        var sut = NewSut(factory);
        var @event = NewEvent();
        await sut.HandleAsync(@event, CancellationToken.None);

        // Same Id arrives again (e.g. after a broker requeue) - must not throw a primary-key
        // violation and must not duplicate the row.
        await sut.HandleAsync(@event, CancellationToken.None);

        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(1, await verify.Clicks.CountAsync());
    }

    [Fact]
    public async Task HandleAsync_RedeliveredAfterMongoFailure_RetriesMongoWriteInsteadOfSkippingIt()
    {
        // Regression: the two writes aren't transactional. If SaveAsync throws (transient Mongo
        // outage) after the Postgres row already committed, a naive "alreadyStored -> skip
        // everything" redelivery guard would permanently lose that click's ClickMeta document,
        // since Postgres already has the row and would short-circuit every future retry too.
        var factory = NewFactory(Guid.NewGuid().ToString());
        var clickMetaStore = new Mock<IClickMetaStore>();
        clickMetaStore.SetupSequence(x => x.SaveAsync(It.IsAny<ClickMeta>(), It.IsAny<CancellationToken>()))
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

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.HandleAsync(@event, CancellationToken.None));
        // Redelivery: same event id: the Postgres row already committed from the first (failed) attempt.
        await sut.HandleAsync(@event, CancellationToken.None);

        clickMetaStore.Verify(x => x.SaveAsync(It.IsAny<ClickMeta>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(1, await verify.Clicks.CountAsync());
    }

    [Fact]
    public async Task HandleAsync_DifferentIds_PersistsBoth()
    {
        var factory = NewFactory(Guid.NewGuid().ToString());
        var sut = NewSut(factory);

        await sut.HandleAsync(NewEvent(), CancellationToken.None);
        await sut.HandleAsync(NewEvent(), CancellationToken.None);

        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(2, await verify.Clicks.CountAsync());
    }
}
