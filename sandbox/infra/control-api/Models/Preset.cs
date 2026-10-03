namespace ControlApi.Models;

// A named, reloadable infra config, optionally paired with one of the saved CustomScenario load
// profiles (ScenarioName) that makes sense to run once the stand is in this shape. Capture/diff/
// apply all happen in the frontend (controlApi already exposes every GET/POST a preset needs) -
// this record is purely what gets persisted.
public record Preset(string Name, InfraConfigSnapshot Config, string? ScenarioName = null);
