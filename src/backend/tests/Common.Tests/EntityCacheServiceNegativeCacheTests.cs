using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Moq;

namespace Common.Tests;

// Negative caching specifically - EntityCacheServiceTests covers the pre-existing positive-path
// behavior (with negative caching left at its own default, enabled), EntityCacheServiceL1Tests
// covers L1. This file is the "bot scanning random hashes" pattern itself.
public class EntityCacheServiceNegativeCacheTests
{
    private static IConfiguration ConfigWith(bool negativeCacheEnabled = true, bool cacheEnabled = true) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cache:L1Enabled"] = "false",
                ["Cache:NegativeEnabled"] = negativeCacheEnabled.ToString(),
                ["Cache:Enabled"] = cacheEnabled.ToString(),
            })
            .Build();

    private static TestEntityCacheService NewSut(Mock<IDistributedCache> cache, bool negativeCacheEnabled = true, bool cacheEnabled = true) =>
        new(cache.Object, ConfigWith(negativeCacheEnabled, cacheEnabled));

    [Fact]
    public async Task GetOrFetch_RepeatedMissOnTheSameKey_OnlyFetchesFromDbOnce()
    {
        // The actual pattern this closes: a caller scanning a hash that never resolves (a bot
        // probing random hashes, or any sustained 404 traffic) must stop reaching the DB after
        // the first miss, within the negative TTL window.
        var cache = new Mock<IDistributedCache>();
        cache.Setup(x => x.GetAsync("test:abc", It.IsAny<CancellationToken>())).ReturnsAsync((byte[]?)null);
        var sut = NewSut(cache);
        var fetchCount = 0;

        Task<TestEntity?> fetchFromDb()
        {
            Interlocked.Increment(ref fetchCount);
            return Task.FromResult<TestEntity?>(null);
        }

        var first = await sut.GetOrFetch("abc", fetchFromDb, CancellationToken.None);
        var second = await sut.GetOrFetch("abc", fetchFromDb, CancellationToken.None);

        Assert.Null(first);
        Assert.Null(second);
        Assert.Equal(1, fetchCount);
    }

    [Fact]
    public async Task GetOrFetch_NegativeCacheDisabled_FetchesFromDbOnEveryMiss()
    {
        var cache = new Mock<IDistributedCache>();
        cache.Setup(x => x.GetAsync("test:abc", It.IsAny<CancellationToken>())).ReturnsAsync((byte[]?)null);
        var sut = NewSut(cache, negativeCacheEnabled: false);
        var fetchCount = 0;

        Task<TestEntity?> fetchFromDb()
        {
            Interlocked.Increment(ref fetchCount);
            return Task.FromResult<TestEntity?>(null);
        }

        await sut.GetOrFetch("abc", fetchFromDb, CancellationToken.None);
        await sut.GetOrFetch("abc", fetchFromDb, CancellationToken.None);

        Assert.Equal(2, fetchCount);
    }

    [Fact]
    public async Task GetOrFetch_EntityCreatedAfterANegativeMiss_StopsReturningNullOnceThePositiveWriteLands()
    {
        // The exact pitfall the original idea called out: negative-cache a 404, then the thing
        // gets created within the negative TTL window - a caller must see it immediately, not
        // keep 404ing for the rest of that window. This isn't closed by evicting the negative
        // marker on write (none happens here) - it's closed by GetOrFetch always checking the
        // positive cache before the negative one, so once Redis has a real entry, the negative
        // marker from moments ago is simply never consulted again for this key.
        var cache = new Mock<IDistributedCache>();
        var positiveValue = (byte[]?)null;
        cache.Setup(x => x.GetAsync("test:abc", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => positiveValue);
        cache.Setup(x => x.SetAsync("test:abc", It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()))
            .Callback<string, byte[], DistributedCacheEntryOptions, CancellationToken>((_, value, _, _) => positiveValue = value)
            .Returns(Task.CompletedTask);
        var sut = NewSut(cache);

        var missResult = await sut.GetOrFetch("abc", () => Task.FromResult<TestEntity?>(null), CancellationToken.None);
        Assert.Null(missResult);

        // Simulates the entity being created elsewhere (e.g. LinkApi's own optimistic cache
        // write) within the negative TTL window - not through GetOrFetch at all.
        await sut.CacheAsync(new TestEntity("abc", "hello"), "abc", CancellationToken.None);

        var hitResult = await sut.GetOrFetch(
            "abc", () => throw new InvalidOperationException("must not re-fetch - the positive cache should already satisfy this"), CancellationToken.None);

        Assert.NotNull(hitResult);
        Assert.Equal("hello", hitResult.Value);
    }

    [Fact]
    public async Task CacheAsync_OverallCacheDisabled_NeverPopulatesNegativeCacheEither()
    {
        var cache = new Mock<IDistributedCache>();
        cache.Setup(x => x.GetAsync("test:abc", It.IsAny<CancellationToken>())).ReturnsAsync((byte[]?)null);
        var sut = NewSut(cache, cacheEnabled: false);
        var fetchCount = 0;

        Task<TestEntity?> fetchFromDb()
        {
            Interlocked.Increment(ref fetchCount);
            return Task.FromResult<TestEntity?>(null);
        }

        await sut.GetOrFetch("abc", fetchFromDb, CancellationToken.None);
        await sut.GetOrFetch("abc", fetchFromDb, CancellationToken.None);

        // Cache:Enabled=false must mean no layer caches anything, negative included - every miss
        // keeps going to the DB, same as the positive path.
        Assert.Equal(2, fetchCount);
    }
}
