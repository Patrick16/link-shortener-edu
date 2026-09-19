namespace ControlApi.Models;

// One point on the load-ramp graph, persisted exactly as the UI edits it (time + VUs at that
// moment) rather than pre-converted to k6 stages - conversion is lossy-free either direction, but
// storing points keeps a saved scenario re-editable in the same graph it was drawn on.
public record ScenarioPoint(int T, int Vus);

// Mode picks which of the two mutually-exclusive shapes below is live - "duration" uses
// TotalDurationSeconds/Points (the ramp graph), "iterations" uses Vus/Iterations (a flat VU count
// running a fixed shared iteration count). Both shapes' fields are always present in storage even
// though only one is meaningful at a time - simpler than two separate scenario record types for
// what's a handful of hand-authored rows in one small JSON file.
public record CustomScenario(
    string Name,
    IReadOnlyList<FlowStep> Steps,
    string Mode,
    int TotalDurationSeconds,
    IReadOnlyList<ScenarioPoint> Points,
    int Vus = 1,
    int Iterations = 100,
    DataPoolRequest? DataPool = null);
