namespace ControlApi.Models;

public record TrafficRequest(string Scenario, int Vus, int DurationSeconds);

public record TrafficScenarioInfo(string Name, string Description);

public record LatencyStats(double Avg, double Min, double Med, double Max, double P90, double P95);

public record CheckResult(string Name, int Passes, int Fails);

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
    string RawOutput);
