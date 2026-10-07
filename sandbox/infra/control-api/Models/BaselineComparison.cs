namespace ControlApi.Models;

// One metric compared between this run and the baseline run - PercentChange is null when Baseline
// is 0 (division is meaningless, e.g. an error rate that went from 0% to something >0%); IsRegression
// is computed directly from Current/Baseline regardless, so the UI never has to re-derive "is this
// bad" from a percent that might not exist. Positive PercentChange always means "the number went up"
// - whether that's good or bad depends on the metric (throughput going up is good, latency isn't),
// which is exactly what IsRegression already resolves, so nothing downstream needs per-metric
// direction logic of its own.
public record MetricComparison(string Name, string Unit, double Current, double Baseline, double? PercentChange, bool IsRegression);

// A run's report compared against the most recent *previous run of the same scenario* - comparing
// against a different scenario's numbers would be meaningless (a resolve-only flow and a full
// register+login+create+click flow have nothing in common to compare). Null on RunSnapshot when no
// earlier run of this scenario exists yet.
public record BaselineComparison(string BaselineRunId, DateTimeOffset BaselineTimestamp, IReadOnlyList<MetricComparison> Metrics);
