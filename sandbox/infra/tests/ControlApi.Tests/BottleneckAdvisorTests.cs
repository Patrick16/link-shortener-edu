using ControlApi.Models;
using ControlApi.Services;

namespace ControlApi.Tests;

public class BottleneckAdvisorTests
{
    private static TrafficReport EmptyReport(long httpRequests = 100, double failedRequestRate = 0, IReadOnlyList<EndpointStatusBreakdown>? statusBreakdown = null) =>
        new(
            Scenario: "flow",
            ExitCode: 0,
            HttpRequests: httpRequests,
            HttpRequestRate: 10,
            FailedRequests: (long)(httpRequests * failedRequestRate),
            FailedRequestRate: failedRequestRate,
            Iterations: httpRequests,
            IterationRate: 10,
            Vus: 10,
            HttpReqDuration: null,
            Checks: [],
            StatusBreakdownByEndpoint: statusBreakdown ?? [],
            RawOutput: "");

    [Fact]
    public void Analyze_NoIssues_ReturnsNoSuspects()
    {
        var verdict = BottleneckAdvisor.Analyze(EmptyReport(), resourceMaxima: [], traceHops: [], pgcatConnections: null);

        Assert.Empty(verdict.Suspects);
    }

    [Fact]
    public void Analyze_PgcatClientsWaiting_FlagsPgcatAsTopSuspect()
    {
        var pgcatConnections = new PgcatConnectionStats([
            new PoolConnectionStats("clicks_db", ClientIdle: 0, ClientActive: 5, ClientWaiting: 3, ServerActive: 5, ServerIdle: 0, ServerUsed: 5),
        ]);

        var verdict = BottleneckAdvisor.Analyze(EmptyReport(), resourceMaxima: [], traceHops: [], pgcatConnections);

        var suspect = Assert.Single(verdict.Suspects);
        Assert.Equal("pgcat-1", suspect.ServiceId);
        Assert.Contains("3", suspect.Evidence);
        Assert.Contains("clicks_db", suspect.Evidence);
    }

    [Fact]
    public void Analyze_NoClientsWaiting_DoesNotFlagPgcat()
    {
        var pgcatConnections = new PgcatConnectionStats([
            new PoolConnectionStats("links_db", ClientIdle: 5, ClientActive: 2, ClientWaiting: 0, ServerActive: 2, ServerIdle: 3, ServerUsed: 2),
        ]);

        var verdict = BottleneckAdvisor.Analyze(EmptyReport(), resourceMaxima: [], traceHops: [], pgcatConnections);

        Assert.Empty(verdict.Suspects);
    }

    // Regression test for the CPU-threshold fix: DockerService's CpuPercent is docker-stats style
    // (100% = one full core), so raw values well above 85 can still be a small fraction of total
    // host capacity on a multi-core machine - the advisor must normalize by core count before
    // comparing against its threshold, not compare the raw value directly.
    [Fact]
    public void Analyze_HighRawCpuButLowShareOfManyCoreHost_DoesNotFlagCpu()
    {
        // On any host with 2+ cores, 150% raw is under the 85%-of-host threshold once normalized -
        // this only needs Environment.ProcessorCount >= 2, true for every real CI/dev machine, and
        // even on a hypothetical single-core box 150/1=150 would incorrectly fire, which is exactly
        // the behavior being guarded against for multi-core hosts (documented known limit, not
        // re-litigated here).
        if (Environment.ProcessorCount < 2)
        {
            return;
        }

        var resourceMaxima = new[] { new NodeResourceMax("rabbitmq", MaxCpuPercent: 150, SumCpuPercent: 150, MaxMemoryUsageBytes: 0, MaxMemoryPercent: 10) };

        var verdict = BottleneckAdvisor.Analyze(EmptyReport(), resourceMaxima, traceHops: [], pgcatConnections: null);

        Assert.Empty(verdict.Suspects);
    }

    [Fact]
    public void Analyze_CpuAtOrAboveHostThreshold_FlagsNode()
    {
        // 85 * ProcessorCount raw = exactly 85% of host capacity once normalized. A single-container
        // service, so SumCpuPercent equals MaxCpuPercent - the evidence text must stay in the plain
        // (non-"combined across replicas") form for this case.
        var rawCpu = 85.0 * Environment.ProcessorCount;
        var resourceMaxima = new[] { new NodeResourceMax("link-api", MaxCpuPercent: rawCpu, SumCpuPercent: rawCpu, MaxMemoryUsageBytes: 0, MaxMemoryPercent: 10) };

        var verdict = BottleneckAdvisor.Analyze(EmptyReport(), resourceMaxima, traceHops: [], pgcatConnections: null);

        var suspect = Assert.Single(verdict.Suspects);
        Assert.Equal("link-api", suspect.ServiceId);
        Assert.Equal("service", suspect.NodeType);
        Assert.DoesNotContain("combined across", suspect.Evidence);
    }

    // Regression test for the Sum-vs-Max aggregation bug: a service scaled to many replicas, each
    // individually well under the threshold, used to never get flagged because the tracker only
    // ever compared the single busiest container's own CPU. SumCpuPercent (replicas summed per
    // poll tick) must be what trips the threshold here, not MaxCpuPercent alone.
    [Fact]
    public void Analyze_CpuSaturatedInAggregateButLowPerReplica_StillFlagsNode()
    {
        var thresholdRawCpu = 85.0 * Environment.ProcessorCount;
        var resourceMaxima = new[]
        {
            new NodeResourceMax(
                "redirect-api",
                MaxCpuPercent: thresholdRawCpu / 4,
                SumCpuPercent: thresholdRawCpu,
                MaxMemoryUsageBytes: 0,
                MaxMemoryPercent: 10),
        };

        var verdict = BottleneckAdvisor.Analyze(EmptyReport(), resourceMaxima, traceHops: [], pgcatConnections: null);

        var suspect = Assert.Single(verdict.Suspects);
        Assert.Equal("redirect-api", suspect.ServiceId);
        Assert.Contains("combined across", suspect.Evidence);
    }

    [Fact]
    public void Analyze_MemoryNearLimit_FlagsNode()
    {
        var resourceMaxima = new[] { new NodeResourceMax("postgres", MaxCpuPercent: 0, SumCpuPercent: 0, MaxMemoryUsageBytes: 0, MaxMemoryPercent: 95) };

        var verdict = BottleneckAdvisor.Analyze(EmptyReport(), resourceMaxima, traceHops: [], pgcatConnections: null);

        var suspect = Assert.Single(verdict.Suspects);
        Assert.Equal("postgres", suspect.ServiceId);
        Assert.Equal("postgres", suspect.NodeType);
    }

    [Fact]
    public void Analyze_SlowTraceHop_FlagsIt()
    {
        var traceHops = new[] { new TraceHopStats("shortener-service", "link.created consume", Count: 50, AvgMs: 200, P95Ms: 350, MaxMs: 500) };

        var verdict = BottleneckAdvisor.Analyze(EmptyReport(), resourceMaxima: [], traceHops, pgcatConnections: null);

        var suspect = Assert.Single(verdict.Suspects);
        Assert.Equal("shortener-service", suspect.ServiceId);
        Assert.Contains("link.created consume", suspect.Evidence);
    }

    [Fact]
    public void Analyze_FastTraceHop_DoesNotFlagIt()
    {
        var traceHops = new[] { new TraceHopStats("link-api", "POST /Links", Count: 50, AvgMs: 2, P95Ms: 5, MaxMs: 10) };

        var verdict = BottleneckAdvisor.Analyze(EmptyReport(), resourceMaxima: [], traceHops, pgcatConnections: null);

        Assert.Empty(verdict.Suspects);
    }

    // Regression test for F1: StatusCount.Label is a human-readable string ("502 Bad Gateway"), not
    // a bare code - a lookup keyed on the exact string "502" always misses and this rule silently
    // never fires. This test fails against the original GetValueOrDefault("502") implementation.
    [Fact]
    public void Analyze_HighFailureRateWithBadGatewayLabels_FlagsNginx()
    {
        var statusBreakdown = new[]
        {
            new EndpointStatusBreakdown("redirect-api.resolve", [
                new StatusCount("502 Bad Gateway", 40),
                new StatusCount("200 OK", 60),
            ]),
        };
        var report = EmptyReport(httpRequests: 100, failedRequestRate: 0.4, statusBreakdown);

        var verdict = BottleneckAdvisor.Analyze(report, resourceMaxima: [], traceHops: [], pgcatConnections: null);

        var suspect = Assert.Single(verdict.Suspects);
        Assert.Equal("nginx", suspect.ServiceId);
        Assert.Contains("40", suspect.Evidence);
    }

    [Fact]
    public void Analyze_HighFailureRateWithInternalServerErrorLabels_FlagsService()
    {
        var statusBreakdown = new[]
        {
            new EndpointStatusBreakdown("link-api.create", [
                new StatusCount("500 Internal Server Error", 30),
                new StatusCount("200 OK", 70),
            ]),
        };
        var report = EmptyReport(httpRequests: 100, failedRequestRate: 0.3, statusBreakdown);

        var verdict = BottleneckAdvisor.Analyze(report, resourceMaxima: [], traceHops: [], pgcatConnections: null);

        var suspect = Assert.Single(verdict.Suspects);
        Assert.Equal("unknown-service", suspect.ServiceId);
        Assert.Contains("30", suspect.Evidence);
    }

    [Fact]
    public void Analyze_LowFailureRate_DoesNotFlagAnything()
    {
        var statusBreakdown = new[]
        {
            new EndpointStatusBreakdown("link-api.create", [
                new StatusCount("500 Internal Server Error", 1),
                new StatusCount("200 OK", 99),
            ]),
        };
        var report = EmptyReport(httpRequests: 100, failedRequestRate: 0.01, statusBreakdown);

        var verdict = BottleneckAdvisor.Analyze(report, resourceMaxima: [], traceHops: [], pgcatConnections: null);

        Assert.Empty(verdict.Suspects);
    }

    [Fact]
    public void Analyze_MultipleSuspects_RanksBySeverityDescending()
    {
        var pgcatConnections = new PgcatConnectionStats([
            new PoolConnectionStats("clicks_db", 0, 5, ClientWaiting: 1, 5, 0, 5),
        ]);
        var traceHops = new[] { new TraceHopStats("link-api", "POST /Links", 10, 200, 350, 400) };

        var verdict = BottleneckAdvisor.Analyze(EmptyReport(), resourceMaxima: [], traceHops, pgcatConnections);

        Assert.Equal(2, verdict.Suspects.Count);
        // pgcat's ClientWaiting rule (severity 100) must outrank the slow-hop rule (severity 70).
        Assert.Equal("pgcat-1", verdict.Suspects[0].ServiceId);
        Assert.Equal("link-api", verdict.Suspects[1].ServiceId);
    }

    [Fact]
    public void Analyze_ChecklistAndVerdictAgreeOnTopSuspect()
    {
        var pgcatConnections = new PgcatConnectionStats([
            new PoolConnectionStats("links_db", 0, 5, ClientWaiting: 2, 5, 0, 5),
        ]);

        var verdict = BottleneckAdvisor.Analyze(EmptyReport(), resourceMaxima: [], traceHops: [], pgcatConnections);

        var summaryStep = Assert.Single(verdict.Checklist, s => s.Title.StartsWith("5."));
        Assert.NotNull(summaryStep.Finding);
        Assert.Contains(verdict.Suspects[0].ServiceId, summaryStep.Finding);
    }

    [Fact]
    public void Analyze_NoRankedSuspects_ChecklistHasNoSummaryStep()
    {
        var verdict = BottleneckAdvisor.Analyze(EmptyReport(), resourceMaxima: [], traceHops: [], pgcatConnections: null);

        Assert.DoesNotContain(verdict.Checklist, s => s.Title.StartsWith("5."));
    }

    // Regression test for making thresholds configurable: a value that's a non-issue under the
    // defaults (70% CPU, below the default 85% threshold) must still flag once a caller passes a
    // stricter configured threshold - proves the parameter is actually read, not just accepted and
    // ignored in favor of the old hardcoded constant.
    [Fact]
    public void Analyze_CustomStricterCpuThreshold_FlagsNodeTheDefaultWouldNotHaveFlagged()
    {
        var rawCpu = 70.0 * Environment.ProcessorCount;
        var resourceMaxima = new[] { new NodeResourceMax("link-api", MaxCpuPercent: rawCpu, SumCpuPercent: rawCpu, MaxMemoryUsageBytes: 0, MaxMemoryPercent: 10) };
        var strictThresholds = BottleneckThresholds.Default with { HighCpuPercent = 60 };

        var defaultVerdict = BottleneckAdvisor.Analyze(EmptyReport(), resourceMaxima, traceHops: [], pgcatConnections: null);
        var strictVerdict = BottleneckAdvisor.Analyze(EmptyReport(), resourceMaxima, traceHops: [], pgcatConnections: null, strictThresholds);

        Assert.Empty(defaultVerdict.Suspects);
        Assert.Single(strictVerdict.Suspects);
    }

    [Fact]
    public void Analyze_NullThresholds_FallsBackToDefault()
    {
        var rawCpu = 85.0 * Environment.ProcessorCount;
        var resourceMaxima = new[] { new NodeResourceMax("link-api", MaxCpuPercent: rawCpu, SumCpuPercent: rawCpu, MaxMemoryUsageBytes: 0, MaxMemoryPercent: 10) };

        var verdict = BottleneckAdvisor.Analyze(EmptyReport(), resourceMaxima, traceHops: [], pgcatConnections: null, thresholds: null);

        Assert.Single(verdict.Suspects);
    }
}
