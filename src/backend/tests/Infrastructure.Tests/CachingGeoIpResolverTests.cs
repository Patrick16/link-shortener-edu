using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Infrastructure.Tests;

public class CachingGeoIpResolverTests
{
    private static Mock<IGeoIpResolver> NewInner(GeoLocation result)
    {
        var inner = new Mock<IGeoIpResolver>();
        inner.Setup(x => x.ResolveAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(result);
        return inner;
    }

    private static CachingGeoIpResolver NewSut(Mock<IGeoIpResolver> inner, Mock<IDistributedCache> cache) =>
        new(inner.Object, cache.Object, NullLogger<CachingGeoIpResolver>.Instance);

    [Fact]
    public async Task ResolveAsync_CacheWriteThrows_StillReturnsTheResolvedResultInsteadOfThrowing()
    {
        // Regression (found during review, confirmed live): a transient Redis failure writing the
        // geo-IP cache entry used to propagate out of ResolveAsync entirely, even though the real
        // lookup had already succeeded - in messaging-mode=grpc this turned a successfully-
        // persisted click into a 502 for the whole redirect, over a pure caching optimization.
        var inner = NewInner(new GeoLocation("DE", "Berlin"));
        var cache = new Mock<IDistributedCache>();
        cache.Setup(x => x.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((byte[]?)null);
        cache.Setup(x => x.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("NOREPLICAS Not enough good replicas to write."));
        var sut = NewSut(inner, cache);

        var result = await sut.ResolveAsync("8.8.8.8");

        Assert.Equal(new GeoLocation("DE", "Berlin"), result);
    }

    [Fact]
    public async Task ResolveAsync_CacheReadThrows_FallsBackToResolvingFreshInsteadOfThrowing()
    {
        var inner = NewInner(new GeoLocation("DE", "Berlin"));
        var cache = new Mock<IDistributedCache>();
        cache.Setup(x => x.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new Exception("connection reset"));
        cache.Setup(x => x.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var sut = NewSut(inner, cache);

        var result = await sut.ResolveAsync("8.8.8.8");

        Assert.Equal(new GeoLocation("DE", "Berlin"), result);
        inner.Verify(x => x.ResolveAsync("8.8.8.8", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ResolveAsync_CacheHit_ReturnsCachedValueWithoutCallingInner()
    {
        var inner = NewInner(new GeoLocation("US", "Springfield"));
        var cache = new Mock<IDistributedCache>();
        cache.Setup(x => x.GetAsync("geoip:8.8.8.8", It.IsAny<CancellationToken>()))
            .ReturnsAsync(System.Text.Encoding.UTF8.GetBytes("""{"Country":"DE","City":"Berlin"}"""));
        var sut = NewSut(inner, cache);

        var result = await sut.ResolveAsync("8.8.8.8");

        Assert.Equal(new GeoLocation("DE", "Berlin"), result);
        inner.Verify(x => x.ResolveAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
