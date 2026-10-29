using Microsoft.Extensions.Logging.Abstractions;
using Polly.CircuitBreaker;

namespace Common.Tests;

public class ResiliencePipelinesTests
{
    // BuildCircuitBreaker's MinimumThroughput is 10 over a 30s SamplingDuration at FailureRatio
    // 0.5 - 10 failing calls in a row is comfortably past both thresholds without needing to wait
    // out any real time.
    private const int CallsPastThreshold = 10;

    [Fact]
    public async Task BuildCircuitBreaker_EnoughFailures_OpensAndStopsCallingTheDelegate()
    {
        var pipeline = ResiliencePipelines.BuildCircuitBreaker("test-dependency", NullLogger.Instance);
        var callCount = 0;

        Task Fail(CancellationToken _)
        {
            callCount++;
            throw new InvalidOperationException("dependency is down");
        }

        for (var i = 0; i < CallsPastThreshold; i++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => pipeline.ExecuteAsync(ct => new ValueTask(Fail(ct)), CancellationToken.None).AsTask());
        }

        // The breaker is now open - the next call must fail immediately with
        // BrokenCircuitException and never reach the delegate at all, which is the entire point:
        // a dead dependency stops being retried on every request until BreakDuration elapses.
        var callCountBeforeProbe = callCount;
        await Assert.ThrowsAsync<BrokenCircuitException>(
            () => pipeline.ExecuteAsync(ct => new ValueTask(Fail(ct)), CancellationToken.None).AsTask());

        Assert.Equal(callCountBeforeProbe, callCount);
    }

    [Fact]
    public async Task BuildCircuitBreaker_OperationCanceledException_NeverCountsTowardOpeningTheBreaker()
    {
        // A caller cancelling (client disconnect, upstream deadline) is not the dependency's
        // fault - ShouldHandle excludes it explicitly. Feed well past the failure threshold with
        // nothing but cancellations, then confirm the breaker is still closed by observing a
        // normal call still reaches the delegate instead of throwing BrokenCircuitException.
        var pipeline = ResiliencePipelines.BuildCircuitBreaker("test-dependency", NullLogger.Instance);

        for (var i = 0; i < CallsPastThreshold * 2; i++)
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                pipeline.ExecuteAsync(
                    ct => throw new OperationCanceledException(), CancellationToken.None).AsTask());
        }

        var result = await pipeline.ExecuteAsync(ct => new ValueTask<string>("ok"), CancellationToken.None);

        Assert.Equal("ok", result);
    }

    [Fact]
    public async Task BuildCircuitBreaker_Disabled_NeverOpensNoMatterHowManyFailures()
    {
        // enabled: false is control-api's "circuit breakers" toggle turned off - BuildCircuitBreaker
        // must hand back a genuine no-op pipeline (ResiliencePipeline.Empty), not just a breaker with
        // an impossibly high threshold, so every call still reaches the delegate and a dead
        // dependency goes back to paying its full timeout on every request.
        var pipeline = ResiliencePipelines.BuildCircuitBreaker("test-dependency", NullLogger.Instance, enabled: false);
        var callCount = 0;

        Task Fail(CancellationToken _)
        {
            callCount++;
            throw new InvalidOperationException("dependency is down");
        }

        for (var i = 0; i < CallsPastThreshold * 2; i++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => pipeline.ExecuteAsync(ct => new ValueTask(Fail(ct)), CancellationToken.None).AsTask());
        }

        // Still reaches the delegate every time - a real breaker would have opened and started
        // throwing BrokenCircuitException well before this many failures.
        Assert.Equal(CallsPastThreshold * 2, callCount);
    }
}
