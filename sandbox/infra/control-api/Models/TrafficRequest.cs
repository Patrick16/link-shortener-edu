namespace ControlApi.Models;

// A stage is k6's own unit (`--stage <duration>:<target>`): "ramp/hold from whatever VUs we're
// currently at to TargetVus over DurationSeconds". The UI's point-based graph is converted into a
// list of these (one per segment between two adjacent points) before the request is sent, so this
// model never needs to know about the graph itself - just what k6's CLI already understands.
public record TrafficStage(int DurationSeconds, int TargetVus);

// One call in the sequence: which EndpointDefinition, and how long to sleep afterward before the
// next step (or the next iteration, if this is the last step) - lets the UI fix the well-known
// create-then-resolve eventual-consistency race on demand (add a pause after Create) instead of
// that being hardcoded into a script, and just as usefully lets it be left at 0 to deliberately
// stress that exact race.
public record FlowStep(string EndpointId, double PauseAfterSeconds = 0);

// Every run goes through the one generic k6-scripts/flow.js now - there's no more "named script"
// concept. Steps is the ordered sequence to call, once per iteration, in that exact order (see
// DockerService.EndpointRegistry) - order matters, since later steps can consume variables earlier
// steps produced (e.g. a "resolve" step needs the "hash" a "create" step earlier in the same
// sequence produced). Scenario is just a display label for the report/UI (e.g. a saved custom
// scenario's own name), not a script name.
//
// Exactly one of (Iterations) or (Stages, falling back to flat Vus+DurationSeconds) drives how the
// run ends - k6's shared-iterations executor (a fixed iteration count, VUs stay flat) and its
// ramping-vus executor (VUs move over time, runs until the ramp's own total duration) are mutually
// exclusive; when Iterations is set it wins and Stages/DurationSeconds are ignored.
public record TrafficRequest(
    string Scenario,
    int Vus,
    int DurationSeconds,
    IReadOnlyList<FlowStep> Steps,
    IReadOnlyList<TrafficStage>? Stages = null,
    int? Iterations = null);

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
