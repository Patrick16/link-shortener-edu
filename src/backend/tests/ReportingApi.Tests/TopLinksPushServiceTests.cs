using Infrastructure;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReportingApi.Realtime;
using StackExchange.Redis;

namespace ReportingApi.Tests;

// F1 regression (backlog item 11's own review): every ReportingApi replica runs its own copy of
// TopLinksPushService, independently subscribed to the same Redis notification channel and
// independently tracking its own local connections - fine before the SignalR Redis backplane
// existed, since the replica that decided to push was always the only one with a local connection
// to deliver to. With the backplane wired, a user connected from two different replicas gets
// double-pushed unless PushAsync's own Redis claim (TryClaimPushAsync) lets only one replica
// through per push interval - these tests exercise that claim directly, not real Redis semantics
// (see RedisRateLimiterTests for the same "mock the call, not the server" approach elsewhere).
public class TopLinksPushServiceTests
{
    // userId is pre-registered as watched (TopLinksSubscriptions.Connected) - PushAsync's own
    // IsWatched re-check (see its comment on why it re-checks after the query) would otherwise
    // short-circuit every test here before the claim/send logic under test ever runs, same as it
    // would for a real caller with no open dashboard connection for that user.
    private static (TopLinksPushService Sut, Mock<IDatabase> Database, Mock<IClientProxy> ClientProxy, Mock<ITopLinksReader> Reader) NewSut(Guid userId)
    {
        var database = new Mock<IDatabase>();
        var multiplexer = new Mock<IConnectionMultiplexer>();
        multiplexer.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(database.Object);

        var reader = new Mock<ITopLinksReader>();
        reader.Setup(r => r.GetTopAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new TopLinkEntry("abc12345", 1)]);

        var clientProxy = new Mock<IClientProxy>();
        var hubClients = new Mock<IHubClients>();
        hubClients.Setup(c => c.Group(It.IsAny<string>())).Returns(clientProxy.Object);
        var hub = new Mock<IHubContext<TopLinksHub>>();
        hub.Setup(h => h.Clients).Returns(hubClients.Object);

        var subscriptions = new TopLinksSubscriptions();
        subscriptions.Connected(userId);

        var sut = new TopLinksPushService(
            multiplexer.Object,
            new TopLinksPushScheduler(),
            subscriptions,
            reader.Object,
            hub.Object,
            NullLogger<TopLinksPushService>.Instance);

        return (sut, database, clientProxy, reader);
    }

    // StackExchange.Redis 3.4.5 has two StringSetAsync(key, value, expiry, when, ...) overloads -
    // a 4-arg one (no CommandFlags) and a 5-arg one (with it). TryClaimPushAsync's own call omits
    // CommandFlags, which resolves to the 4-arg overload, not the 5-arg one defaulted - confirmed
    // via reflection against the installed package after this mismatch silently made every test
    // here fall through to Moq's unconfigured-call default (false) instead of throwing.
    private static void SetupClaim(Mock<IDatabase> database, bool claimed) =>
        database.Setup(d => d.StringSetAsync(
                It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<When>()))
            .ReturnsAsync(claimed);

    [Fact]
    public async Task PushAsync_ClaimSucceeds_QueriesAndSendsToTheGroup()
    {
        var userId = Guid.NewGuid();
        var (sut, database, clientProxy, reader) = NewSut(userId);
        SetupClaim(database, claimed: true);

        await sut.PushAsync(userId, CancellationToken.None);

        reader.Verify(r => r.GetTopAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        clientProxy.Verify(
            c => c.SendCoreAsync(TopLinksHub.TopLinksMethod, It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PushAsync_ClaimAlreadyHeldByAnotherReplica_SkipsQueryAndSend()
    {
        // The actual double-push bug this fix closes: a sibling replica (modeled here as a
        // StringSetAsync NX that returns false, meaning the key already exists) already claimed
        // this user's push for the current interval - this replica must neither re-query
        // ClickHouse nor send its own copy of the update.
        var userId = Guid.NewGuid();
        var (sut, database, clientProxy, reader) = NewSut(userId);
        SetupClaim(database, claimed: false);

        await sut.PushAsync(userId, CancellationToken.None);

        reader.Verify(r => r.GetTopAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
        clientProxy.Verify(
            c => c.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task PushAsync_ClaimCallThrows_FailsOpenAndStillPushes()
    {
        // A Redis outage for the claim call itself must degrade to this service's pre-backplane
        // behavior (every watching replica pushes independently - at worst a duplicate update),
        // never to "no replica pushes anything" - that would be strictly worse than the bug this
        // claim exists to fix.
        var userId = Guid.NewGuid();
        var (sut, database, clientProxy, reader) = NewSut(userId);
        database.Setup(d => d.StringSetAsync(
                It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<When>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "redis down"));

        await sut.PushAsync(userId, CancellationToken.None);

        reader.Verify(r => r.GetTopAsync(It.IsAny<Guid>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
        clientProxy.Verify(
            c => c.SendCoreAsync(TopLinksHub.TopLinksMethod, It.IsAny<object?[]>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
