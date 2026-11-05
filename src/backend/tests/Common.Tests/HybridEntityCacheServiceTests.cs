using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;

namespace Common.Tests;

public class TestHybridEntityCacheService(HybridCache cache) : HybridEntityCacheService<TestEntity>(cache)
{
    protected override string Key(string id) => $"test:{id}";
}

// Real HybridCache (via AddHybridCache().BuildServiceProvider()), not a mock - unlike
// EntityCacheService, HybridCache's own GetOrCreateAsync/SetAsync/RemoveAsync are the thing under
// test here (the L1+L2 layering and single-flight coalescing a mock would just assume away). No
// IDistributedCache is registered, so every test below runs L1 (in-process) only - that's enough
// to exercise HybridEntityCacheService's own mapping onto HybridCache's API; which backing store
// HybridCache itself picks for L2 is Microsoft's concern, not this class's.
public class HybridEntityCacheServiceTests
{
    private static HybridCache BuildCache()
    {
        var services = new ServiceCollection();
        services.AddHybridCache();
        return services.BuildServiceProvider().GetRequiredService<HybridCache>();
    }

    [Fact]
    public async Task GetCachedAsync_CacheMiss_ReturnsNull()
    {
        var sut = new TestHybridEntityCacheService(BuildCache());

        var result = await sut.GetCachedAsync("abc", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task CacheAsync_ThenGetCachedAsync_ReturnsTheSameEntity()
    {
        var sut = new TestHybridEntityCacheService(BuildCache());
        var entity = new TestEntity("abc", "hello");

        await sut.CacheAsync(entity, "abc", CancellationToken.None);
        var result = await sut.GetCachedAsync("abc", CancellationToken.None);

        Assert.Equal(entity, result);
    }

    [Fact]
    public async Task InvalidateCacheAsync_RemovesTheCachedEntity()
    {
        var sut = new TestHybridEntityCacheService(BuildCache());
        await sut.CacheAsync(new TestEntity("abc", "hello"), "abc", CancellationToken.None);

        await sut.InvalidateCacheAsync("abc");

        Assert.Null(await sut.GetCachedAsync("abc", CancellationToken.None));
    }

    [Fact]
    public async Task GetOrFetch_CacheMiss_FetchesFromDbAndCachesTheResult()
    {
        var sut = new TestHybridEntityCacheService(BuildCache());
        var entity = new TestEntity("abc", "hello");
        var calls = 0;
        Task<TestEntity?> Fetch()
        {
            calls++;
            return Task.FromResult<TestEntity?>(entity);
        }

        var result1 = await sut.GetOrFetch("abc", Fetch, CancellationToken.None);
        var result2 = await sut.GetOrFetch("abc", Fetch, CancellationToken.None);

        Assert.Equal(entity, result1);
        Assert.Equal(entity, result2);
        Assert.Equal(1, calls);
    }

    // The coalescing EntityCacheService hand-rolls via ConcurrentDictionary<string, Lazy<Task<T?>>>
    // (see its own GetOrFetch comment) is exactly what HybridCache's GetOrCreateAsync absorbs - this
    // is the behavior HybridEntityCacheService exists to get "for free".
    [Fact]
    public async Task GetOrFetch_ConcurrentMissesOnSameKey_FetchFromDbRunsOnlyOnce()
    {
        var sut = new TestHybridEntityCacheService(BuildCache());
        var calls = 0;
        var gate = new SemaphoreSlim(0);
        async Task<TestEntity?> Fetch()
        {
            Interlocked.Increment(ref calls);
            await gate.WaitAsync();
            return new TestEntity("abc", "hello");
        }

        var task1 = sut.GetOrFetch("abc", Fetch, CancellationToken.None);
        var task2 = sut.GetOrFetch("abc", Fetch, CancellationToken.None);
        // Give both callers a chance to reach the factory before releasing it, so a would-be
        // second, uncoalesced call has every opportunity to happen before this asserts it didn't.
        await Task.Delay(100);
        gate.Release(2);
        var results = await Task.WhenAll(task1, task2);

        Assert.Equal(new TestEntity("abc", "hello"), results[0]);
        Assert.Equal(new TestEntity("abc", "hello"), results[1]);
        Assert.Equal(1, calls);
    }

    // Confirmed by direct testing against the real package (see HybridEntityCacheService's own
    // class-level comment): unlike the assumption a reader might reasonably make, HybridCache DOES
    // cache a null factory result - it just doesn't get the legacy class's short, separate
    // negative-cache TTL.
    [Fact]
    public async Task GetOrFetch_FetchReturnsNull_CachesTheNullResultAndDoesNotRefetch()
    {
        var sut = new TestHybridEntityCacheService(BuildCache());
        var calls = 0;
        Task<TestEntity?> Fetch()
        {
            calls++;
            return Task.FromResult<TestEntity?>(null);
        }

        var result1 = await sut.GetOrFetch("missing", Fetch, CancellationToken.None);
        var result2 = await sut.GetOrFetch("missing", Fetch, CancellationToken.None);

        Assert.Null(result1);
        Assert.Null(result2);
        Assert.Equal(1, calls);
    }
}
