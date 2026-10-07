using ControlApi.Models;

namespace ControlApi.Services;

// Pure comparison of one run's TrafficReport against a prior run's - no I/O, no run-history
// lookup of its own (TrafficRunCoordinator finds the baseline candidate and hands both reports
// here), same "static, explainable, testable in isolation" shape as BottleneckAdvisor.
public static class BaselineComparer
{
    public static BaselineComparison Compare(TrafficReport current, TrafficReport baseline, string baselineRunId, DateTimeOffset baselineTimestamp)
    {
        var metrics = new List<MetricComparison>
        {
            Compare("Throughput", "req/s", current.HttpRequestRate, baseline.HttpRequestRate, higherIsBetter: true),
            // *100: FailedRequestRate is a 0..1 fraction (see BottleneckAdvisor's identical reading
            // of it) - comparing/displaying it as percentage points is what "5% -> 8%" actually means
            // to a reader, not "0.05 -> 0.08".
            Compare("Error rate", "%", current.FailedRequestRate * 100, baseline.FailedRequestRate * 100, higherIsBetter: false),
        };

        // Both sides need a k6 summary.json-derived LatencyStats to compare p95 - null on either one
        // (e.g. the run or its baseline failed before k6 produced a summary) just drops this metric
        // instead of comparing against a fabricated zero.
        if (current.HttpReqDuration is { } currentDuration && baseline.HttpReqDuration is { } baselineDuration)
        {
            metrics.Add(Compare("p95 latency", "ms", currentDuration.P95, baselineDuration.P95, higherIsBetter: false));
        }

        return new BaselineComparison(baselineRunId, baselineTimestamp, metrics);
    }

    // PercentChange is left null when baseline is 0 - "infinite % increase" isn't a meaningful
    // number to show (and a 0-baseline is a legitimate case, e.g. an error rate that was genuinely
    // zero last run). IsRegression still has an answer either way: higherIsBetter flips which
    // direction counts as worse, computed once here so nothing downstream needs to know per-metric
    // which way is bad.
    private static MetricComparison Compare(string name, string unit, double current, double baseline, bool higherIsBetter)
    {
        double? percentChange = baseline != 0 ? (current - baseline) / baseline * 100 : null;
        var isRegression = higherIsBetter ? current < baseline : current > baseline;
        return new MetricComparison(name, unit, current, baseline, percentChange, isRegression);
    }
}
