using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Common.Tests;

public record TestEntity(string Id, string Value);

public class TestEntityCacheService(IDistributedCache cache, IConfiguration configuration)
    : EntityCacheService<TestEntity>(cache, configuration, NullLogger<EntityCacheService<TestEntity>>.Instance)
{
    protected override string Key(string id) => $"test:{id}";
}

public class EntityCacheServiceTests
{
    private static IConfiguration ConfigWith(bool? cacheEnabled = null)
    {
        var data = cacheEnabled is null
            ? new Dictionary<string, string?>()
            : new Dictionary<string, string?> { ["Cache:Enabled"] = cacheEnabled.Value.ToString() };

        return new ConfigurationBuilder().AddInMemoryCollection(data).Build();
    }

    [Fact]
    public async Task GetCachedAsync_CacheMiss_ReturnsNull()
    {
        var cache = new Mock<IDistributedCache>();
        cache.Setup(x => x.GetAsync("test:abc", It.IsAny<CancellationToken>()))
            .ReturnsAsync((byte[]?)null);
        var sut = new TestEntityCacheService(cache.Object, ConfigWith());

        var result = await sut.GetCachedAsync("abc", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetCachedAsync_CacheHit_DeserializesEntity()
    {
        var cache = new Mock<IDistributedCache>();
        var json = System.Text.Encoding.UTF8.GetBytes("""{"id":"abc","value":"hello"}""");
        cache.Setup(x => x.GetAsync("test:abc", It.IsAny<CancellationToken>()))
            .ReturnsAsync(json);
        var sut = new TestEntityCacheService(cache.Object, ConfigWith());

        var result = await sut.GetCachedAsync("abc", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("abc", result!.Id);
        Assert.Equal("hello", result.Value);
    }

    [Fact]
    public async Task GetCachedAsync_CacheDisabled_NeverCallsDistributedCache()
    {
        var cache = new Mock<IDistributedCache>();
        var sut = new TestEntityCacheService(cache.Object, ConfigWith(cacheEnabled: false));

        var result = await sut.GetCachedAsync("abc", CancellationToken.None);

        Assert.Null(result);
        cache.Verify(x => x.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetCachedAsync_UnderlyingCacheThrows_ReturnsNullInsteadOfThrowing()
    {
        // A Redis outage must degrade to "cache miss", not fail the request that triggered it.
        var cache = new Mock<IDistributedCache>();
        cache.Setup(x => x.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("redis is down"));
        var sut = new TestEntityCacheService(cache.Object, ConfigWith());

        var result = await sut.GetCachedAsync("abc", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task CacheAsync_CacheDisabled_NeverCallsDistributedCache()
    {
        var cache = new Mock<IDistributedCache>();
        var sut = new TestEntityCacheService(cache.Object, ConfigWith(cacheEnabled: false));

        await sut.CacheAsync(new TestEntity("abc", "hello"), "abc", CancellationToken.None);

        cache.Verify(
            x => x.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CacheAsync_UnderlyingCacheThrows_DoesNotPropagate()
    {
        var cache = new Mock<IDistributedCache>();
        cache.Setup(x => x.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("redis is down"));
        var sut = new TestEntityCacheService(cache.Object, ConfigWith());

        await sut.CacheAsync(new TestEntity("abc", "hello"), "abc", CancellationToken.None);
        // No exception - populating the cache is an optimization, a failure must not bubble up.
    }

    [Fact]
    public async Task InvalidateCacheAsync_CacheDisabled_NeverCallsDistributedCache()
    {
        var cache = new Mock<IDistributedCache>();
        var sut = new TestEntityCacheService(cache.Object, ConfigWith(cacheEnabled: false));

        await sut.InvalidateCacheAsync("abc");

        cache.Verify(x => x.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetOrFetch_CacheMiss_FetchesFromDbAndPopulatesCache()
    {
        var cache = new Mock<IDistributedCache>();
        cache.Setup(x => x.GetAsync("test:abc", It.IsAny<CancellationToken>())).ReturnsAsync((byte[]?)null);
        var sut = new TestEntityCacheService(cache.Object, ConfigWith());
        var entity = new TestEntity("abc", "hello");

        var result = await sut.GetOrFetch("abc", () => Task.FromResult<TestEntity?>(entity), CancellationToken.None);

        Assert.Same(entity, result);
        cache.Verify(
            x => x.SetAsync("test:abc", It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetOrFetch_CacheHit_NeverCallsFetchFromDb()
    {
        var cache = new Mock<IDistributedCache>();
        var json = System.Text.Encoding.UTF8.GetBytes("""{"id":"abc","value":"hello"}""");
        cache.Setup(x => x.GetAsync("test:abc", It.IsAny<CancellationToken>())).ReturnsAsync(json);
        var sut = new TestEntityCacheService(cache.Object, ConfigWith());
        var fetchCalled = false;

        var result = await sut.GetOrFetch("abc", () =>
        {
            fetchCalled = true;
            return Task.FromResult<TestEntity?>(new TestEntity("abc", "should-not-be-used"));
        }, CancellationToken.None);

        Assert.False(fetchCalled);
        Assert.Equal("hello", result!.Value);
    }

    [Fact]
    public async Task GetOrFetch_ConcurrentMissesOnSameKey_FetchFromDbRunsOnlyOnce()
    {
        // Three callers miss the same key before the first fetch has resolved. Moq's ReturnsAsync
        // completes GetCachedAsync synchronously, so each GetOrFetch call runs up through GetOrAdd
        // on the calling thread before yielding - by the time the second and third calls reach
        // GetOrAdd, the first caller's fetch is already registered as in-flight, so they must join
        // it instead of calling fetchFromDb again.
        var cache = new Mock<IDistributedCache>();
        cache.Setup(x => x.GetAsync("test:abc", It.IsAny<CancellationToken>())).ReturnsAsync((byte[]?)null);
        var sut = new TestEntityCacheService(cache.Object, ConfigWith());
        var entity = new TestEntity("abc", "hello");
        var fetchCount = 0;
        var releaseFetch = new TaskCompletionSource<TestEntity?>();

        Task<TestEntity?> fetchFromDb()
        {
            Interlocked.Increment(ref fetchCount);
            return releaseFetch.Task;
        }

        var call1 = sut.GetOrFetch("abc", fetchFromDb, CancellationToken.None);
        var call2 = sut.GetOrFetch("abc", fetchFromDb, CancellationToken.None);
        var call3 = sut.GetOrFetch("abc", fetchFromDb, CancellationToken.None);
        releaseFetch.SetResult(entity);
        var results = await Task.WhenAll(call1, call2, call3);

        Assert.Equal(1, fetchCount);
        Assert.All(results, r => Assert.Same(entity, r));
        cache.Verify(
            x => x.SetAsync("test:abc", It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetOrFetch_SequentialMissesOnSameKey_EachCallFetchesIndependently()
    {
        // Sanity check for the coalescing above: once a fetch has fully completed and been removed
        // from the in-flight table, a later miss on the same key must fetch again rather than reusing
        // a stale completed Task forever.
        var cache = new Mock<IDistributedCache>();
        cache.Setup(x => x.GetAsync("test:abc", It.IsAny<CancellationToken>())).ReturnsAsync((byte[]?)null);
        var sut = new TestEntityCacheService(cache.Object, ConfigWith());
        var fetchCount = 0;

        Task<TestEntity?> fetchFromDb()
        {
            Interlocked.Increment(ref fetchCount);
            return Task.FromResult<TestEntity?>(new TestEntity("abc", $"fetch-{fetchCount}"));
        }

        await sut.GetOrFetch("abc", fetchFromDb, CancellationToken.None);
        await sut.GetOrFetch("abc", fetchFromDb, CancellationToken.None);

        Assert.Equal(2, fetchCount);
    }

    [Fact]
    public async Task GetOrFetch_DbMiss_ReturnsNullAndDoesNotPopulateCache()
    {
        var cache = new Mock<IDistributedCache>();
        cache.Setup(x => x.GetAsync("test:abc", It.IsAny<CancellationToken>())).ReturnsAsync((byte[]?)null);
        var sut = new TestEntityCacheService(cache.Object, ConfigWith());

        var result = await sut.GetOrFetch("abc", () => Task.FromResult<TestEntity?>(null), CancellationToken.None);

        Assert.Null(result);
        cache.Verify(
            x => x.SetAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<DistributedCacheEntryOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
