namespace ControlApi.Models;

// One point on the load-ramp graph, persisted exactly as the UI edits it (time + VUs at that
// moment) rather than pre-converted to k6 stages - conversion is lossy-free either direction, but
// storing points keeps a saved scenario re-editable in the same graph it was drawn on.
public record ScenarioPoint(int T, int Vus);

public record CustomScenario(string Name, IReadOnlyList<string> Endpoints, int TotalDurationSeconds, IReadOnlyList<ScenarioPoint> Points);
