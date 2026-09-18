namespace ControlApi.Models;

// A stage is k6's own unit (`--stage <duration>:<target>`): "ramp/hold from whatever VUs we're
// currently at to TargetVus over DurationSeconds". The UI's point-based graph is converted into a
// list of these (one per segment between two adjacent points) before the request is sent, so this
// model never needs to know about the graph itself - just what k6's CLI already understands.
public record TrafficStage(int DurationSeconds, int TargetVus);

// Stages, when present and non-empty, replace the flat Vus+DurationSeconds run entirely: Vus
// becomes k6's start-VUs (`--vus`) and DurationSeconds is ignored in favor of the stages' own
// total. Kept both shapes on one request rather than two endpoints since everything downstream
// (progress reporting, the report itself) is identical either way.
//
// Endpoints, when present and non-empty, replace the named-script lookup ({Scenario}.js) with the
// one generic k6-scripts/custom.js, which picks a random endpoint from this list each iteration -
// see DockerService.KnownEndpoints for the allowed values. Scenario is still sent along in this
// mode purely as a label for the report/UI (e.g. a saved custom scenario's name), not a script name.
public record TrafficRequest(
    string Scenario,
    int Vus,
    int DurationSeconds,
    IReadOnlyList<TrafficStage>? Stages = null,
    IReadOnlyList<string>? Endpoints = null);

public record TrafficScenarioInfo(string Name, string Description);

public record LatencyStats(double Avg, double Min, double Med, double Max, double P90, double P95);

public record CheckResult(string Name, int Passes, int Fails);

// One HTTP status code (or "0" for a request that never got a response at all - connection
// refused/reset/timeout) and how many requests landed on it, across every request the run made.
// Only codes k6 was told to track via a threshold show up at all (see TrackedStatusCodes in
// DockerService) and only ones that actually occurred (Count > 0) make it into a report.
public record StatusCount(string Label, long Count);

public record TrafficReport(
    string Scenario,
    long ExitCode,
    long HttpRequests,
    double HttpRequestRate,
    long FailedRequests,
    double FailedRequestRate,
    long Iterations,
    double IterationRate,
    int Vus,
    LatencyStats? HttpReqDuration,
    IReadOnlyList<CheckResult> Checks,
    IReadOnlyList<StatusCount> StatusBreakdown,
    string RawOutput);
