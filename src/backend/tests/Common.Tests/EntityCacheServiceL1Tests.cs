using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Polly;

namespace Common.Tests;

// L1 (in-process) behavior specifically - EntityCacheServiceTests covers the pre-existing Redis/L2
// fail-open behavior with L1 deliberately disabled (see that file's own note on why).
public class EntityCacheServiceL1Tests
{
    private static IConfiguration ConfigWith(bool l1CacheEnabled, bool cacheEnabled = true) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cache:L1Enabled"] = l1CacheEnabled.ToString(),
                ["Cache:Enabled"] = cacheEnabled.ToString(),
            })
            .Build();

    private static TestEntityCacheService NewSut(Mock<IDistributedCache> cache, bool l1CacheEnabled, bool cacheEnabled = true) =>
        new(cache.Object, ConfigWith(l1CacheEnabled, cacheEnabled));

    [Fact]
    public async Task GetOrFetch_L1Enabled_SecondCallNeverTouchesRedisOrDb()
    {
        var cache = new Mock<IDistributedCache>();
        cache.Setup(x => x.GetAsync("test:abc", It.IsAny<CancellationToken>())).ReturnsAsync((byte[]?)null);
        var sut = NewSut(cache, l1CacheEnabled: true);
        var fetchCount = 0;

        Task<TestEntity?> fetchFromDb()
        {
            Interlocked.Increment(ref fetchCount);
            return Task.FromResult<TestEntity?>(new TestEntity("abc", "hello"));
        }

        var first = await sut.GetOrFetch("abc", fetchFromDb, CancellationToken.None);
        var second = await sut.GetOrFetch("abc", fetchFromDb, CancellationToken.None);

        Assert.Equal(1, fetchCount);
        Assert.Equal("hello", first!.Value);
        Assert.Equal("hello", second!.Value);
        // Only the first call's cache-aside read should ever have reached Redis - the second was
        // served entirely from L1, never falling through to GetCachedAsync at all.
        cache.Verify(x => x.GetAsync("test:abc", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetOrFetch_L1Disabled_EachCallGoesThroughRedis()
    {
        // Same scenario as the test above, L1 off - this is the pre-existing (pre-L1) behavior,
        // kept here as a direct contrast rather than only living in EntityCacheServiceTests.
        var cache = new Mock<IDistributedCache>();
        cache.Setup(x => x.GetAsync("test:abc", It.IsAny<CancellationToken>())).ReturnsAsync((byte[]?)null);
        var sut = NewSut(cache, l1CacheEnabled: false);
        var fetchCount = 0;

        Task<TestEntity?> fetchFromDb()
        {
            Interlocked.Increment(ref fetchCount);
            return Task.FromResult<TestEntity?>(new TestEntity("abc", $"fetch-{fetchCount}"));
        }

        await sut.GetOrFetch("abc", fetchFromDb, CancellationToken.None);
        await sut.GetOrFetch("abc", fetchFromDb, CancellationToken.None);

        Assert.Equal(2, fetchCount);
        cache.Verify(x => x.GetAsync("test:abc", It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task InvalidateCacheAsync_EvictsL1OnThisInstance()
    {
        var cache = new Mock<IDistributedCache>();
        cache.Setup(x => x.GetAsync("test:abc", It.IsAny<CancellationToken>())).ReturnsAsync((byte[]?)null);
        var sut = NewSut(cache, l1CacheEnabled: true);
        var fetchCount = 0;

        Task<TestEntity?> fetchFromDb()
        {
            Interlocked.Increment(ref fetchCount);
            return Task.FromResult<TestEntity?>(new TestEntity("abc", $"fetch-{fetchCount}"));
        }

        await sut.GetOrFetch("abc", fetchFromDb, CancellationToken.None);
        await sut.InvalidateCacheAsync("abc");
        await sut.GetOrFetch("abc", fetchFromDb, CancellationToken.None);

        // Without the invalidation evicting L1 too (not just Redis), the second GetOrFetch would
        // have been served from the now-stale L1 entry and fetchCount would still be 1 - this is
        // exactly the regression InvalidateCacheAsync's own _l1Cache.Remove call prevents on the
        // SAME replica (see EntityCacheService's class comment on why other replicas don't get this).
        Assert.Equal(2, fetchCount);
    }

    [Fact]
    public async Task CacheAsync_OverallCacheDisabled_NeverPopulatesL1Either()
    {
        // PopulateL1 is gated on _cacheEnabled too, not just _l1CacheEnabled - turning overall
        // caching off must mean no layer caches anything, L1 included, or the "disable caching"
        // toggle would stop being an honest "every read goes to the DB" demo.
        var cache = new Mock<IDistributedCache>();
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var sut = new LinkCacheServiceL1TestHelper(cache.Object, ConfigWith(l1CacheEnabled: true, cacheEnabled: false), memoryCache);

        await sut.CacheAsync(new TestEntity("abc", "hello"), "abc", CancellationToken.None);

        Assert.False(memoryCache.TryGetValue("test:abc", out TestEntity? _));
    }

    // Exposes the MemoryCache instance so CacheAsync_OverallCacheDisabled_NeverPopulatesL1Either can
    // inspect it directly - TestEntityCacheService (in EntityCacheServiceTests.cs) builds its own
    // internally and doesn't expose it, which is fine for every other test here (they only ever
    // observe L1 indirectly, through fetch counts and cache.Verify).
    private sealed class LinkCacheServiceL1TestHelper(IDistributedCache cache, IConfiguration configuration, MemoryCache memoryCache)
        : EntityCacheService<TestEntity>(cache, configuration, NullLogger<EntityCacheService<TestEntity>>.Instance, ResiliencePipeline.Empty, memoryCache)
    {
        protected override string Key(string id) => $"test:{id}";
    }
}
