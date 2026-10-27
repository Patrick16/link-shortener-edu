using Common;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Polly;
using StackExchange.Redis;

namespace Infrastructure.Tests;

public class RedisRateLimiterTests
{
    private static (RedisRateLimiter Sut, Mock<IDatabase> Database) NewSut()
    {
        var database = new Mock<IDatabase>();
        var multiplexer = new Mock<IConnectionMultiplexer>();
        multiplexer.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(database.Object);
        var sut = new RedisRateLimiter(
            multiplexer.Object,
            ResiliencePipeline.Empty,
            NullLogger<RedisRateLimiter>.Instance);
        return (sut, database);
    }

    private static void SetupScriptResult(Mock<IDatabase> database, long returnValue) =>
        database.Setup(d => d.ScriptEvaluateAsync(
                It.IsAny<string>(), It.IsAny<RedisKey[]>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisResult.Create((RedisValue)returnValue));

    [Fact]
    public async Task CheckAsync_CountAtOrBelowLimit_ReturnsAllowed()
    {
        var (sut, database) = NewSut();
        SetupScriptResult(database, 3);

        var outcome = await sut.CheckAsync("ratelimit:test:1.2.3.4", permitLimit: 3, TimeSpan.FromSeconds(60), CancellationToken.None);

        Assert.Equal(RateLimitOutcome.Allowed, outcome);
    }

    [Fact]
    public async Task CheckAsync_CountAboveLimit_ReturnsDenied()
    {
        var (sut, database) = NewSut();
        SetupScriptResult(database, 4);

        var outcome = await sut.CheckAsync("ratelimit:test:1.2.3.4", permitLimit: 3, TimeSpan.FromSeconds(60), CancellationToken.None);

        Assert.Equal(RateLimitOutcome.Denied, outcome);
    }

    [Fact]
    public async Task CheckAsync_RedisThrows_ReturnsRedisUnavailableInsteadOfPropagating()
    {
        // The caller (RedisRateLimitFilter) decides fail-open vs. fail-closed - this layer must
        // never pick for them by throwing or by silently mapping a Redis failure to Allowed/Denied.
        var (sut, database) = NewSut();
        database.Setup(d => d.ScriptEvaluateAsync(
                It.IsAny<string>(), It.IsAny<RedisKey[]>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
            .ThrowsAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "redis down"));

        var outcome = await sut.CheckAsync("ratelimit:test:1.2.3.4", permitLimit: 3, TimeSpan.FromSeconds(60), CancellationToken.None);

        Assert.Equal(RateLimitOutcome.RedisUnavailable, outcome);
    }

    [Fact]
    public async Task CheckAsync_PassesWindowAsMillisecondsAndTheGivenKey_ToTheScript()
    {
        var (sut, database) = NewSut();
        database.Setup(d => d.ScriptEvaluateAsync(
                It.IsAny<string>(),
                It.Is<RedisKey[]>(keys => keys.Length == 1 && keys[0] == "ratelimit:test:1.2.3.4"),
                It.Is<RedisValue[]>(values => values.Length == 1 && (long)values[0] == 60_000),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(RedisResult.Create((RedisValue)1L));

        var outcome = await sut.CheckAsync("ratelimit:test:1.2.3.4", permitLimit: 3, TimeSpan.FromSeconds(60), CancellationToken.None);

        Assert.Equal(RateLimitOutcome.Allowed, outcome);
    }
}
