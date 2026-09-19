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

    private static LinkCreatedConsumer NewSut(IDbContextFactory<DatabaseContext> factory) =>
        new(Mock.Of<IMessageConsumer>(), factory, NullLogger<LinkCreatedConsumer>.Instance);

    [Fact]
    public async Task HandleAsync_NewLink_PersistsToDatabase()
    {
        var factory = NewFactory(Guid.NewGuid().ToString());
        var sut = NewSut(factory);

        await sut.HandleAsync(NewEvent(), CancellationToken.None);

        await using var verify = await factory.CreateDbContextAsync();
        var stored = await verify.Links.SingleAsync();
        Assert.Equal("abc12345", stored.Hash);
        Assert.Equal("https://example.com", stored.OriginalLink);
    }

    [Fact]
    public async Task HandleAsync_RedeliveredMessage_IsSkippedWithoutError()
    {
        var factory = NewFactory(Guid.NewGuid().ToString());
        var sut = NewSut(factory);
        var @event = NewEvent();
        await sut.HandleAsync(@event, CancellationToken.None);

        // Same hash arrives again (e.g. after a broker requeue) - must not throw a unique-key
        // violation and must not duplicate the row.
        await sut.HandleAsync(@event, CancellationToken.None);

        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(1, await verify.Links.CountAsync());
    }

    [Fact]
    public async Task HandleAsync_DifferentHashes_PersistsBoth()
    {
        var factory = NewFactory(Guid.NewGuid().ToString());
        var sut = NewSut(factory);

        await sut.HandleAsync(NewEvent("hash0001"), CancellationToken.None);
        await sut.HandleAsync(NewEvent("hash0002"), CancellationToken.None);

        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(2, await verify.Links.CountAsync());
    }
}
