using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using StackExchange.Redis;

namespace Infrastructure.Tests;

public class RedisWarmupServiceTests
{
    [Fact]
    public async Task StartAsync_MultiplexerFactoryThrows_DoesNotFailHostStartup()
    {
        // Regression: resolving the singleton runs ConnectionMultiplexer.Connect, which throws when
        // Sentinel can't resolve the master yet - that used to abort host startup (redirect-api exit 139).
        var services = new Mock<IServiceProvider>();
        services.Setup(s => s.GetService(typeof(IConnectionMultiplexer)))
            .Throws(new InvalidOperationException("Sentinel: Failed connecting to configured primary"));

        var sut = new RedisWarmupService(Mock.Of<IDistributedCache>(), services.Object, NullLogger<RedisWarmupService>.Instance);

        await sut.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_CacheThrows_DoesNotFailHostStartup()
    {
        var cache = new Mock<IDistributedCache>();
        cache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("redis down"));

        var sut = new RedisWarmupService(cache.Object, Mock.Of<IServiceProvider>(), NullLogger<RedisWarmupService>.Instance);

        await sut.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_Success_CompletesWithoutError()
    {
        var cache = new Mock<IDistributedCache>();
        cache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((byte[]?)null);
        // No IConnectionMultiplexer registration (mirrors LinkApi, which never registers one) -
        // GetService returning null must be handled without touching PingAsync.
        var services = new Mock<IServiceProvider>();
        services.Setup(s => s.GetService(typeof(IConnectionMultiplexer))).Returns((object?)null);

        var sut = new RedisWarmupService(cache.Object, services.Object, NullLogger<RedisWarmupService>.Instance);

        await sut.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_CacheNeverCompletes_ReturnsWithinTimeoutBoundInstead()
    {
        // F1 regression: without the Task.WhenAny race, Redis being merely *slow* (not immediately
        // failing) used to leave StartAsync - and therefore the whole container's startup, since
        // Kestrel doesn't accept connections until every IHostedService.StartAsync returns - hanging
        // for as long as the attempt took (observed elsewhere in this investigation: ~8s average,
        // up to ~67s, under a connection-establishment burst), with HostOptions.StartupTimeout
        // unbounded by default. A never-completing TaskCompletionSource simulates the worst case.
        var neverCompletes = new TaskCompletionSource<byte[]?>();
        var cache = new Mock<IDistributedCache>();
        cache.Setup(c => c.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(neverCompletes.Task);
        var services = new Mock<IServiceProvider>();
        services.Setup(s => s.GetService(typeof(IConnectionMultiplexer))).Returns((object?)null);
        var sut = new RedisWarmupService(cache.Object, services.Object, NullLogger<RedisWarmupService>.Instance);

        var startTask = sut.StartAsync(CancellationToken.None);
        var completed = await Task.WhenAny(startTask, Task.Delay(TimeSpan.FromSeconds(8)));

        Assert.Same(startTask, completed);
    }
}
