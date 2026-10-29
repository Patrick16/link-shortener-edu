using Common.Models;
using Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Infrastructure.Tests;

// Covers HedgedClickMetaQueryService's fallback path (nothing to hedge against) and RaceAsync's
// own hedge-delay/winner/cancel-the-loser mechanism directly, via a dedicated SUT instance
// (RaceAsync is `internal`) rather than through a real Mongo node - no Testcontainers dependency
// for this file.
public class HedgedClickMetaQueryServiceTests
{
    private static HedgedClickMetaQueryService NewSut() =>
        new(Mock.Of<IClickMetaQueryService>(), [], TimeSpan.Zero, NullLogger<HedgedClickMetaQueryService>.Instance);

    [Fact]
    public async Task RaceAsync_FirstFinishesWithinHedgeDelay_NeverStartsSecond()
    {
        var sut = NewSut();
        var secondStarted = false;

        var result = await sut.RaceAsync(
            _ => Task.FromResult(1),
            _ => { secondStarted = true; return Task.FromResult(2); },
            hedgeDelay: TimeSpan.FromSeconds(30),
            CancellationToken.None);

        Assert.Equal(1, result);
        Assert.False(secondStarted);
    }

    [Fact]
    public async Task RaceAsync_FirstSlowerThanHedgeDelay_SecondWinsAndFirstIsCancelled()
    {
        var sut = NewSut();
        var firstCancelled = false;
        var firstTcs = new TaskCompletionSource<int>();

        var result = await sut.RaceAsync(
            ct =>
            {
                ct.Register(() => firstCancelled = true);
                return firstTcs.Task;
            },
            _ => Task.FromResult(2),
            hedgeDelay: TimeSpan.FromMilliseconds(10),
            CancellationToken.None);

        Assert.Equal(2, result);
        Assert.True(firstCancelled);
        Assert.False(firstTcs.Task.IsCompleted);
    }

    [Fact]
    public async Task RaceAsync_FirstFaultsAfterHedgeFires_FallsThroughToSecond()
    {
        var sut = NewSut();

        var result = await sut.RaceAsync(
            _ => Task.FromException<int>(new InvalidOperationException("first replica down")),
            _ => Task.FromResult(2),
            hedgeDelay: TimeSpan.FromMilliseconds(10),
            CancellationToken.None);

        Assert.Equal(2, result);
    }

    [Fact]
    public async Task RaceAsync_BothFault_RethrowsAFailure()
    {
        var sut = NewSut();

        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.RaceAsync(
            _ => Task.FromException<int>(new InvalidOperationException("first down")),
            _ => Task.FromException<int>(new InvalidOperationException("second down too")),
            hedgeDelay: TimeSpan.FromMilliseconds(10),
            CancellationToken.None));
    }

    [Fact]
    public async Task GetRecentAsync_FewerThanTwoHedgeNodes_UsesFallbackDirectly()
    {
        var expected = new List<ClickDetail> { new(Guid.NewGuid(), DateTime.UtcNow, null, null, null, null, null, null, null) };
        var fallback = new Mock<IClickMetaQueryService>();
        fallback.Setup(f => f.GetRecentAsync("abc12345", It.IsAny<Guid>(), 20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        var oneHedgeNode = new Mock<IClickMetaQueryService>();
        var sut = new HedgedClickMetaQueryService(fallback.Object, [oneHedgeNode.Object], TimeSpan.FromMilliseconds(15), NullLogger<HedgedClickMetaQueryService>.Instance);

        var result = await sut.GetRecentAsync("abc12345", Guid.NewGuid(), 20, CancellationToken.None);

        Assert.Same(expected, result);
        oneHedgeNode.Verify(h => h.GetRecentAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetRecentAsync_NoHedgeNodesAtAll_UsesFallbackDirectly()
    {
        var expected = new List<ClickDetail>();
        var fallback = new Mock<IClickMetaQueryService>();
        fallback.Setup(f => f.GetRecentAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        var sut = new HedgedClickMetaQueryService(fallback.Object, [], TimeSpan.FromMilliseconds(15), NullLogger<HedgedClickMetaQueryService>.Instance);

        var result = await sut.GetRecentAsync("abc12345", Guid.NewGuid(), 20, CancellationToken.None);

        Assert.Same(expected, result);
    }

    [Fact]
    public async Task PingAsync_DelegatesToFallback()
    {
        var fallback = new Mock<IClickMetaQueryService>();
        fallback.Setup(f => f.PingAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var sut = new HedgedClickMetaQueryService(fallback.Object, [], TimeSpan.FromMilliseconds(15), NullLogger<HedgedClickMetaQueryService>.Instance);

        var result = await sut.PingAsync(CancellationToken.None);

        Assert.True(result);
        fallback.Verify(f => f.PingAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
