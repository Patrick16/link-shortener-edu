using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;

namespace Infrastructure.Tests;

public class LinkDeletionMarkerStoreTests
{
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
        var sut = new RedisLinkDeletionMarkerStore(multiplexer.Object, NullLogger<RedisLinkDeletionMarkerStore>.Instance);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        // Propagates, same as every other budget-covered call in this request (Fetch,
        // _cache.GetOrFetch) - the surrounding catch deliberately excludes OperationCanceledException
        // (see the "fail open" catches just below) so RedirectController's own budget-exceeded
        // handling sees this as a real cancellation, not as a false "no tombstone found".
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.GetAsync("abc12345", cts.Token));
    }
}
