using Common.Models;
using Contracts.Events;
using Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ShortenerService;

namespace ShortenerService.Tests;

// SQLite in-memory, not EF Core's InMemory provider: HandleAsync uses ExecuteUpdateAsync (a single
// atomic "UPDATE ... SET ClickCount = ClickCount + 1"), which InMemory doesn't support at all
// (it only translates LINQ against SQL-backed providers). Each test opens its own private
// ":memory:" connection - SQLite tears the database down once the last connection to it closes, so
// the connection has to stay open for the test's whole lifetime, not just schema creation.
public sealed class ClickTrackedConsumerTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public ClickTrackedConsumerTests() => _connection.Open();

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

    private static ClickTrackedEvent NewEvent(string hash = "abc12345") => new()
    {
        Id = Guid.NewGuid(),
        Hash = hash,
        InboundLink = "https://short.example/abc12345",
        OutboundLink = "https://example.com",
        ClickedAt = DateTime.UtcNow,
        UserAgent = "",
        Referrer = "",
    };

    private static ClickTrackedConsumer NewSut(IDbContextFactory<DatabaseContext> factory) =>
        new(Mock.Of<IMessageConsumer>(), factory, NullLogger<ClickTrackedConsumer>.Instance);

    [Fact]
    public async Task HandleAsync_KnownHash_IncrementsClickCount()
    {
        var factory = NewFactory();
        await using (var seed = await factory.CreateDbContextAsync())
        {
            seed.Links.Add(new Link("abc12345", "https://example.com", "abc12345", DateTime.UtcNow, null));
            await seed.SaveChangesAsync();
        }
        var sut = NewSut(factory);

        await sut.HandleAsync(NewEvent(), CancellationToken.None);

        await using var verify = await factory.CreateDbContextAsync();
        var stored = await verify.Links.SingleAsync();
        Assert.Equal(1, stored.ClickCount);
    }

    [Fact]
    public async Task HandleAsync_MultipleEvents_AccumulatesCount()
    {
        var factory = NewFactory();
        await using (var seed = await factory.CreateDbContextAsync())
        {
            seed.Links.Add(new Link("abc12345", "https://example.com", "abc12345", DateTime.UtcNow, null));
            await seed.SaveChangesAsync();
        }
        var sut = NewSut(factory);

        await sut.HandleAsync(NewEvent(), CancellationToken.None);
        await sut.HandleAsync(NewEvent(), CancellationToken.None);
        await sut.HandleAsync(NewEvent(), CancellationToken.None);

        await using var verify = await factory.CreateDbContextAsync();
        var stored = await verify.Links.SingleAsync();
        Assert.Equal(3, stored.ClickCount);
    }

    [Fact]
    public async Task HandleAsync_UnknownHash_DoesNotThrow()
    {
        var factory = NewFactory();
        var sut = NewSut(factory);

        var exception = await Record.ExceptionAsync(() => sut.HandleAsync(NewEvent("never-created"), CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact]
    public async Task HandleAsync_DifferentHashes_OnlyIncrementsTheMatchingLink()
    {
        var factory = NewFactory();
        await using (var seed = await factory.CreateDbContextAsync())
        {
            seed.Links.Add(new Link("hash0001", "https://a.example", "hash0001", DateTime.UtcNow, null));
            seed.Links.Add(new Link("hash0002", "https://b.example", "hash0002", DateTime.UtcNow, null));
            await seed.SaveChangesAsync();
        }
        var sut = NewSut(factory);

        await sut.HandleAsync(NewEvent("hash0001"), CancellationToken.None);

        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(1, (await verify.Links.SingleAsync(x => x.Hash == "hash0001")).ClickCount);
        Assert.Equal(0, (await verify.Links.SingleAsync(x => x.Hash == "hash0002")).ClickCount);
    }
}
