using Common.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ShortenerService;

namespace ShortenerService.Tests;

// SQLite in-memory, not EF Core's InMemory provider: FlushOnceAsync uses ExecuteUpdateAsync (a
// single atomic "UPDATE ... SET ClickCount = ClickCount + n"), which InMemory doesn't support at all
// (it only translates LINQ against SQL-backed providers). Each test opens its own private
// ":memory:" connection - SQLite tears the database down once the last connection to it closes, so
// the connection has to stay open for the test's whole lifetime, not just schema creation.
public sealed class ClickCountFlushWorkerTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public ClickCountFlushWorkerTests() => _connection.Open();

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

    private static ClickCountFlushWorker NewSut(ClickCountAggregator aggregator, IDbContextFactory<DatabaseContext> factory) =>
        new(aggregator, factory, NullLogger<ClickCountFlushWorker>.Instance);

    [Fact]
    public async Task FlushOnceAsync_NothingBuffered_DoesNotTouchTheDatabase()
    {
        var factory = NewFactory();
        var aggregator = new ClickCountAggregator();
        var sut = NewSut(aggregator, factory);

        // No assertion needed beyond "doesn't throw" - CreateDbContextAsync is never even called
        // when there's nothing to flush (the early return in FlushOnceAsync), so there's nothing to
        // verify against a real database either.
        await sut.FlushOnceAsync(CancellationToken.None);
    }

    [Fact]
    public async Task FlushOnceAsync_BufferedIncrement_AppliesItToTheMatchingLink()
    {
        var factory = NewFactory();
        await using (var seed = await factory.CreateDbContextAsync())
        {
            seed.Links.Add(new Link("abc12345", "https://example.com", "abc12345", DateTime.UtcNow, null));
            await seed.SaveChangesAsync();
        }
        var aggregator = new ClickCountAggregator();
        aggregator.Add("abc12345", 5);
        var sut = NewSut(aggregator, factory);

        await sut.FlushOnceAsync(CancellationToken.None);

        await using var verify = await factory.CreateDbContextAsync();
        var stored = await verify.Links.SingleAsync();
        Assert.Equal(5, stored.ClickCount);
    }

    [Fact]
    public async Task FlushOnceAsync_MultipleHashes_OnlyUpdatesTheMatchingLinks()
    {
        var factory = NewFactory();
        await using (var seed = await factory.CreateDbContextAsync())
        {
            seed.Links.Add(new Link("hash0001", "https://a.example", "hash0001", DateTime.UtcNow, null));
            seed.Links.Add(new Link("hash0002", "https://b.example", "hash0002", DateTime.UtcNow, null));
            await seed.SaveChangesAsync();
        }
        var aggregator = new ClickCountAggregator();
        aggregator.Add("hash0001", 2);
        var sut = NewSut(aggregator, factory);

        await sut.FlushOnceAsync(CancellationToken.None);

        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(2, (await verify.Links.SingleAsync(x => x.Hash == "hash0001")).ClickCount);
        Assert.Equal(0, (await verify.Links.SingleAsync(x => x.Hash == "hash0002")).ClickCount);
    }

    [Fact]
    public async Task FlushOnceAsync_UnknownHash_DoesNotThrow()
    {
        var factory = NewFactory();
        var aggregator = new ClickCountAggregator();
        aggregator.Add("never-created", 1);
        var sut = NewSut(aggregator, factory);

        var exception = await Record.ExceptionAsync(() => sut.FlushOnceAsync(CancellationToken.None));

        Assert.Null(exception);
    }

    [Fact]
    public async Task FlushOnceAsync_DrainsTheAggregator_SoASecondFlushWithNoNewActivityIsANoop()
    {
        var factory = NewFactory();
        await using (var seed = await factory.CreateDbContextAsync())
        {
            seed.Links.Add(new Link("abc12345", "https://example.com", "abc12345", DateTime.UtcNow, null));
            await seed.SaveChangesAsync();
        }
        var aggregator = new ClickCountAggregator();
        aggregator.Add("abc12345", 3);
        var sut = NewSut(aggregator, factory);

        await sut.FlushOnceAsync(CancellationToken.None);
        await sut.FlushOnceAsync(CancellationToken.None);

        await using var verify = await factory.CreateDbContextAsync();
        var stored = await verify.Links.SingleAsync();
        Assert.Equal(3, stored.ClickCount);
    }
}
