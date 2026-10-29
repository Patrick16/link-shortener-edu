using Common;
using Common.Models;
using Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ShortenerService;
using StackExchange.Redis;

namespace ShortenerService.Tests;

// Covers the sustained Redis leader election this worker uses (acquire/renew/lose), and that the
// sweep itself is gated on holding leadership - not the sweep's own raw Postgres SQL, which was
// already untested before this worker switched mechanisms (Postgres-specific RETURNING/now(),
// no SQLite equivalent worth faking just for this).
public class LinkExpiryWorkerTests
{
    private static (LinkExpiryWorker Sut, Mock<IDatabase> Database, Mock<IDbContextFactory<DatabaseContext>> Factory) NewSut()
    {
        var database = new Mock<IDatabase>();
        var multiplexer = new Mock<IConnectionMultiplexer>();
        multiplexer.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(database.Object);
        var factory = new Mock<IDbContextFactory<DatabaseContext>>();
        var deletionMarkers = new Mock<ILinkDeletionMarkerStore>();
        var cache = new Mock<IEntityCacheService<Link>>();

        var sut = new LinkExpiryWorker(
            factory.Object,
            multiplexer.Object,
            deletionMarkers.Object,
            cache.Object,
            NullLogger<LinkExpiryWorker>.Instance);

        return (sut, database, factory);
    }

    // See TopLinksPushServiceTests.SetupClaim's own comment: LinkExpiryWorker's claim call omits
    // CommandFlags, which resolves to the 4-arg StringSetAsync overload, not a 5-arg one defaulted.
    private static void SetupClaim(Mock<IDatabase> database, bool acquired) =>
        database.Setup(d => d.StringSetAsync(
                It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<When>()))
            .ReturnsAsync(acquired);

    private static void SetupRenew(Mock<IDatabase> database, long returnValue) =>
        database.Setup(d => d.ScriptEvaluateAsync(
                It.IsAny<string>(), It.IsAny<RedisKey[]>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisResult.Create((RedisValue)returnValue));

    [Fact]
    public async Task LeaderElectionTickAsync_KeyIsFree_AcquiresLeadership()
    {
        var (sut, database, _) = NewSut();
        SetupClaim(database, acquired: true);

        var isLeader = await sut.LeaderElectionTickAsync(CancellationToken.None);

        Assert.True(isLeader);
    }

    [Fact]
    public async Task LeaderElectionTickAsync_KeyHeldByAnotherReplica_StaysNotLeader()
    {
        var (sut, database, _) = NewSut();
        SetupClaim(database, acquired: false);

        var isLeader = await sut.LeaderElectionTickAsync(CancellationToken.None);

        Assert.False(isLeader);
    }

    [Fact]
    public async Task LeaderElectionTickAsync_AlreadyLeader_RenewSucceeds_StaysLeader()
    {
        var (sut, database, _) = NewSut();
        SetupClaim(database, acquired: true);
        await sut.LeaderElectionTickAsync(CancellationToken.None);
        SetupRenew(database, returnValue: 1);

        var isLeader = await sut.LeaderElectionTickAsync(CancellationToken.None);

        Assert.True(isLeader);
    }

    [Fact]
    public async Task LeaderElectionTickAsync_AlreadyLeader_LeaseNotRenewed_LosesLeadership()
    {
        // The renew script returns 0 when the key's value no longer matches this replica - e.g. the
        // lease already expired and another replica claimed it first.
        var (sut, database, _) = NewSut();
        SetupClaim(database, acquired: true);
        await sut.LeaderElectionTickAsync(CancellationToken.None);
        SetupRenew(database, returnValue: 0);

        var isLeader = await sut.LeaderElectionTickAsync(CancellationToken.None);

        Assert.False(isLeader);
    }

    [Fact]
    public async Task LeaderElectionTickAsync_RedisThrows_AssumesNotLeader()
    {
        // Fail safe, not fail open - see this worker's own comment on why an uncoordinated sweep
        // (every replica ticking at once the moment Redis recovers) is the wrong default here.
        var (sut, database, _) = NewSut();
        SetupClaim(database, acquired: true);
        await sut.LeaderElectionTickAsync(CancellationToken.None);
        database.Setup(d => d.ScriptEvaluateAsync(
                It.IsAny<string>(), It.IsAny<RedisKey[]>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "redis down"));

        var isLeader = await sut.LeaderElectionTickAsync(CancellationToken.None);

        Assert.False(isLeader);
    }

    [Fact]
    public async Task RunOnceAsync_NotLeader_NeverOpensADatabaseContext()
    {
        var (sut, _, factory) = NewSut();

        await sut.RunOnceAsync(CancellationToken.None);

        factory.Verify(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()), Times.Never);
    }
}
