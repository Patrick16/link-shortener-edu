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
    public async Task HandleBatchAsync_ConcurrentWorkersRaceSameHash_BothSucceedAndPersistOnce()
    {
        // shortener-service is a scalable, competing-consumers service: a redelivered message can
        // land on a different replica than the one that first saw it, so two independent
        // IDbContextFactory-backed DbContexts (not this file's usual shared EF Core InMemory
        // database, which serializes everything through one in-process lock and so could never
        // actually race) can both run HandleBatchAsync for the same hash at once - both see it as
        // "not yet stored", both try to insert it, and exactly one hits a real unique-key violation
        // against Postgres. A real on-disk SQLite file with Pooling=False and a busy timeout (same
        // reasoning as RefreshTokenServiceTests.RotateAsync_TwoConcurrentCallsWithSameToken...) is
        // what lets the two calls genuinely overlap through SQLite's own row-level locking, rather
        // than this test's own await ordering deciding who "wins".
        var dbPath = Path.Combine(Path.GetTempPath(), $"link-created-race-{Guid.NewGuid()}.db");
        try
        {
            DbContextOptions<DatabaseContext> NewOptions() =>
                new DbContextOptionsBuilder<DatabaseContext>()
                    .UseSqlite($"Data Source={dbPath};Pooling=False;Default Timeout=5")
                    .Options;

            await using (var bootstrap = new DatabaseContext(NewOptions()))
            {
                await bootstrap.Database.EnsureCreatedAsync();
            }

            IDbContextFactory<DatabaseContext> NewFileFactory()
            {
                var factory = new Mock<IDbContextFactory<DatabaseContext>>();
                factory.Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                    .ReturnsAsync(() => new DatabaseContext(NewOptions()));
                return factory.Object;
            }

            var sutA = NewSut(NewFileFactory());
            var sutB = NewSut(NewFileFactory());
            var @event = NewEvent();

            var taskA = sutA.HandleBatchAsync(NewBatch(@event), CancellationToken.None);
            var taskB = sutB.HandleBatchAsync(NewBatch(@event), CancellationToken.None);
            var outcomes = await Task.WhenAll(taskA, taskB);

            // Neither call throws the unique-key violation out to the caller - it's caught and
            // retried against the now-current state (see HandleBatchAsync's own DbUpdateException
            // catch), not left to propagate into RabbitMqConsumer.HandleBatchFailureAsync, which
            // would nack-with-requeue every other, unrelated message riding along in either real
            // batch just because of this one hash's race.
            Assert.All(outcomes, o => Assert.Equal(BatchOutcome.Success, o));

            await using var verify = new DatabaseContext(NewOptions());
            Assert.Equal(1, await verify.Links.CountAsync());
        }
        finally
        {
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
        }
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

    [Fact]
    public async Task HandleBatchAsync_SameAliasDifferentOriginalLink_ReportsPoisonWithAliasReason()
    {
        // Not a redelivery of the same message - a genuine conflict: two different links tried to
        // claim the same custom alias (the tail-risk case this whole content-comparison fix exists
        // for). Must not be silently dropped the way a true duplicate is.
        var factory = NewFactory(Guid.NewGuid().ToString());
        var sut = NewSut(factory);
        var winner = NewEvent("my-alias") with { HasCustomAlias = true };
        await sut.HandleBatchAsync(NewBatch(winner), CancellationToken.None);

        var loser = winner with { OriginalLink = "https://example.com/different-target" };
        var outcome = await sut.HandleBatchAsync(NewBatch(loser), CancellationToken.None);

        var poison = Assert.Single(outcome.PoisonMessages);
        Assert.Contains("Alias 'my-alias' is already in use by a different link", poison.Reason);
        await using var verify = await factory.CreateDbContextAsync();
        Assert.Equal(1, await verify.Links.CountAsync());
        Assert.Equal(winner.OriginalLink, (await verify.Links.SingleAsync()).OriginalLink);
    }

    [Fact]
    public async Task HandleBatchAsync_SameGeneratedHashDifferentOriginalLink_ReportsPoisonWithHashCollisionReason()
    {
        // Same conflict shape as the alias case above, but the existing (winning) row was never a
        // custom alias - the reason text must say so, since this would mean an actual random-hash
        // collision (near-impossible, not expected) rather than a routine alias race.
        var factory = NewFactory(Guid.NewGuid().ToString());
        var sut = NewSut(factory);
        var winner = NewEvent("abc12345");
        await sut.HandleBatchAsync(NewBatch(winner), CancellationToken.None);

        var loser = winner with { OriginalLink = "https://example.com/different-target" };
        var outcome = await sut.HandleBatchAsync(NewBatch(loser), CancellationToken.None);

        var poison = Assert.Single(outcome.PoisonMessages);
        Assert.Contains("unexpected for a generated hash", poison.Reason);
    }

    [Fact]
    public async Task HandleBatchAsync_ConcurrentWorkersRaceSameHashDifferentOriginalLink_OneSucceedsOnePoisoned()
    {
        // Same real-race scaffold as HandleBatchAsync_ConcurrentWorkersRaceSameHash_
        // BothSucceedAndPersistOnce (real on-disk SQLite, two independent DbContexts genuinely
        // overlapping) - but this time the two racers are different links claiming the same alias,
        // not two copies of the same message. The loser must come back as poison, not a silent
        // second "success" that actually persisted nothing.
        var dbPath = Path.Combine(Path.GetTempPath(), $"link-created-conflict-race-{Guid.NewGuid()}.db");
        try
        {
            DbContextOptions<DatabaseContext> NewOptions() =>
                new DbContextOptionsBuilder<DatabaseContext>()
                    .UseSqlite($"Data Source={dbPath};Pooling=False;Default Timeout=5")
                    .Options;

            await using (var bootstrap = new DatabaseContext(NewOptions()))
            {
                await bootstrap.Database.EnsureCreatedAsync();
            }

            IDbContextFactory<DatabaseContext> NewFileFactory()
            {
                var factory = new Mock<IDbContextFactory<DatabaseContext>>();
                factory.Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                    .ReturnsAsync(() => new DatabaseContext(NewOptions()));
                return factory.Object;
            }

            var sutA = NewSut(NewFileFactory());
            var sutB = NewSut(NewFileFactory());
            var eventA = NewEvent("contested-alias") with { HasCustomAlias = true, OriginalLink = "https://example.com/a" };
            var eventB = eventA with { OriginalLink = "https://example.com/b" };

            var taskA = sutA.HandleBatchAsync(NewBatch(eventA), CancellationToken.None);
            var taskB = sutB.HandleBatchAsync(NewBatch(eventB), CancellationToken.None);
            var outcomes = await Task.WhenAll(taskA, taskB);

            Assert.Single(outcomes, o => o == BatchOutcome.Success);
            var poisoned = Assert.Single(outcomes, o => o.PoisonMessages.Count > 0);
            Assert.Contains("already in use by a different link", Assert.Single(poisoned.PoisonMessages).Reason);

            await using var verify = new DatabaseContext(NewOptions());
            Assert.Equal(1, await verify.Links.CountAsync());
        }
        finally
        {
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
        }
    }
}
