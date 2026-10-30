using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Polly;
using StackExchange.Redis;

namespace Infrastructure.Tests;

public class LinkDeletionMarkerStoreTests
{
    // Empty config -> Cache:Enabled defaults to true (same "no key set => enabled" convention as
    // EntityCacheServiceTests), and ResiliencePipeline.Empty since this test exercises the
    // cancellation-propagation try/catch below, not the circuit breaker itself.
    private static IConfiguration EnabledConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();

    [Fact]
    public async Task GetAsync_RedisCallNeverCompletes_RespectsCancellationInsteadOfHangingForever()
    {
        // F2 regression: StackExchange.Redis's StringGetAsync has no CancellationToken overload at
        // all, so without WaitAsync racing it against the token, a caller's deadline - e.g.
        // RedirectController's request budget - was silently ignored here no matter how slow a
        // connected-but-hanging Redis got (found in review). A never-completing TaskCompletionSource
        // simulates exactly that: the real call is still running, just never going to finish on its
        // own.
        var neverCompletes = new TaskCompletionSource<RedisValue>();
        var database = new Mock<IDatabase>();
        database.Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
            .Returns(neverCompletes.Task);
        var multiplexer = new Mock<IConnectionMultiplexer>();
        multiplexer.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(database.Object);
        var sut = new RedisLinkDeletionMarkerStore(
            multiplexer.Object, EnabledConfig(), ResiliencePipeline.Empty, NullLogger<RedisLinkDeletionMarkerStore>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        // Propagates, same as every other budget-covered call in this request (Fetch,
        // _cache.GetOrFetch) - the surrounding catch deliberately excludes OperationCanceledException
        // (see the "fail open" catches just below) so RedirectController's own budget-exceeded
        // handling sees this as a real cancellation, not as a false "no tombstone found".
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.GetAsync("abc12345", cts.Token));
    }

    [Fact]
    public async Task TryMarkOptimisticallyAsync_RedisCallNeverCompletes_RespectsCancellationInsteadOfHangingForever()
    {
        // Same gap as GetAsync above - StringSetAsync has no cancellable overload either, so this
        // method's own ExecuteAsync lambda needs its own .WaitAsync(ct), not just GetAsync's.
        // Overload matters for Moq: TryMarkOptimisticallyAsync's (key, value, expiry, when) call
        // resolves to the 4-arg StringSetAsync(RedisKey, RedisValue, TimeSpan?, When) overload, not
        // the 5-arg one with a trailing CommandFlags - confirmed via reflection against
        // IDatabaseAsync, since StackExchange.Redis overloads this method several different ways.
        var neverCompletes = new TaskCompletionSource<bool>();
        var database = new Mock<IDatabase>();
        database.Setup(d => d.StringSetAsync(
                It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<When>()))
            .Returns(neverCompletes.Task);
        var multiplexer = new Mock<IConnectionMultiplexer>();
        multiplexer.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(database.Object);
        var sut = new RedisLinkDeletionMarkerStore(
            multiplexer.Object, EnabledConfig(), ResiliencePipeline.Empty, NullLogger<RedisLinkDeletionMarkerStore>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sut.TryMarkOptimisticallyAsync("abc12345", DeletedReason.Manual, cts.Token));
    }

    [Fact]
    public async Task ConfirmAsync_RedisCallNeverCompletes_RespectsCancellationInsteadOfHangingForever()
    {
        // Same gap as GetAsync above - StringSetAsync has no cancellable overload either, so this
        // method's own ExecuteAsync lambda needs its own .WaitAsync(ct), not just GetAsync's.
        // ConfirmAsync's bare (key, value) call resolves to the Expiration/ValueCondition overload
        // (the only one where every parameter past value has a default) - confirmed the same way as
        // TryMarkOptimisticallyAsync's test above.
        var neverCompletes = new TaskCompletionSource<bool>();
        var database = new Mock<IDatabase>();
        database.Setup(d => d.StringSetAsync(
                It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<Expiration>(), It.IsAny<ValueCondition>(), It.IsAny<CommandFlags>()))
            .Returns(neverCompletes.Task);
        var multiplexer = new Mock<IConnectionMultiplexer>();
        multiplexer.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(database.Object);
        var sut = new RedisLinkDeletionMarkerStore(
            multiplexer.Object, EnabledConfig(), ResiliencePipeline.Empty, NullLogger<RedisLinkDeletionMarkerStore>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sut.ConfirmAsync("abc12345", DeletedReason.Manual, DateTime.UtcNow, cts.Token));
    }
}
