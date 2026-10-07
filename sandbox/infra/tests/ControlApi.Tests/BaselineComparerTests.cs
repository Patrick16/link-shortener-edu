using ControlApi.Models;
using ControlApi.Services;

namespace ControlApi.Tests;

public class BaselineComparerTests
{
    private static TrafficReport Report(double httpRequestRate = 10, double failedRequestRate = 0, LatencyStats? duration = null) =>
        new(
            Scenario: "flow",
            ExitCode: 0,
            HttpRequests: 100,
            HttpRequestRate: httpRequestRate,
            FailedRequests: (long)(100 * failedRequestRate),
            FailedRequestRate: failedRequestRate,
            Iterations: 100,
            IterationRate: httpRequestRate,
            Vus: 10,
            HttpReqDuration: duration,
            Checks: [],
            StatusBreakdownByEndpoint: [],
            RawOutput: "");

    private static LatencyStats Latency(double p95) => new(Avg: p95 / 2, Min: 1, Med: p95 / 2, Max: p95 * 2, P90: p95 * 0.9, P95: p95);

    [Fact]
    public void Compare_HigherThroughput_IsNotARegression()
    {
        var current = Report(httpRequestRate: 120);
        var baseline = Report(httpRequestRate: 100);

        var comparison = BaselineComparer.Compare(current, baseline, "baseline-id", DateTimeOffset.UtcNow);

        var throughput = Assert.Single(comparison.Metrics, m => m.Name == "Throughput");
        Assert.False(throughput.IsRegression);
        Assert.Equal(20, throughput.PercentChange!.Value, precision: 5);
    }

    [Fact]
    public void Compare_LowerThroughput_IsARegression()
    {
        var current = Report(httpRequestRate: 80);
        var baseline = Report(httpRequestRate: 100);

        var comparison = BaselineComparer.Compare(current, baseline, "baseline-id", DateTimeOffset.UtcNow);

        var throughput = Assert.Single(comparison.Metrics, m => m.Name == "Throughput");
        Assert.True(throughput.IsRegression);
        Assert.Equal(-20, throughput.PercentChange!.Value, precision: 5);
    }

    [Fact]
    public void Compare_HigherErrorRate_IsARegression()
    {
        var current = Report(failedRequestRate: 0.10);
        var baseline = Report(failedRequestRate: 0.05);

        var comparison = BaselineComparer.Compare(current, baseline, "baseline-id", DateTimeOffset.UtcNow);

        var errorRate = Assert.Single(comparison.Metrics, m => m.Name == "Error rate");
        Assert.True(errorRate.IsRegression);
        // Reported in percentage points (10, 5), not raw fractions (0.10, 0.05).
        Assert.Equal(10, errorRate.Current, precision: 5);
        Assert.Equal(5, errorRate.Baseline, precision: 5);
    }

    [Fact]
    public void Compare_BaselineErrorRateWasZero_PercentChangeIsNullButStillFlagsRegression()
    {
        var current = Report(failedRequestRate: 0.02);
        var baseline = Report(failedRequestRate: 0);

        var comparison = BaselineComparer.Compare(current, baseline, "baseline-id", DateTimeOffset.UtcNow);

        var errorRate = Assert.Single(comparison.Metrics, m => m.Name == "Error rate");
        Assert.Null(errorRate.PercentChange);
        Assert.True(errorRate.IsRegression);
    }

    [Fact]
    public void Compare_BothZero_IsNotARegressionAndPercentChangeIsNull()
    {
        var current = Report(failedRequestRate: 0);
        var baseline = Report(failedRequestRate: 0);

        var comparison = BaselineComparer.Compare(current, baseline, "baseline-id", DateTimeOffset.UtcNow);

        var errorRate = Assert.Single(comparison.Metrics, m => m.Name == "Error rate");
        Assert.Null(errorRate.PercentChange);
        Assert.False(errorRate.IsRegression);
    }

    [Fact]
    public void Compare_HigherP95Latency_IsARegression()
    {
        var current = Report(duration: Latency(360));
        var baseline = Report(duration: Latency(300));

        var comparison = BaselineComparer.Compare(current, baseline, "baseline-id", DateTimeOffset.UtcNow);

        var latency = Assert.Single(comparison.Metrics, m => m.Name == "p95 latency");
        Assert.True(latency.IsRegression);
        Assert.Equal(20, latency.PercentChange!.Value, precision: 5);
    }

    [Fact]
    public void Compare_EitherSideMissingDuration_OmitsLatencyMetric()
    {
        var current = Report(duration: null);
        var baseline = Report(duration: Latency(300));

        var comparison = BaselineComparer.Compare(current, baseline, "baseline-id", DateTimeOffset.UtcNow);

        Assert.DoesNotContain(comparison.Metrics, m => m.Name == "p95 latency");
    }

    [Fact]
    public void Compare_CarriesBaselineIdAndTimestampThrough()
    {
        var timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var comparison = BaselineComparer.Compare(Report(), Report(), "run-abc123", timestamp);

        Assert.Equal("run-abc123", comparison.BaselineRunId);
        Assert.Equal(timestamp, comparison.BaselineTimestamp);
    }
}
