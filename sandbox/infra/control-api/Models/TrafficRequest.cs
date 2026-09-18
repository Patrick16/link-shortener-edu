namespace ControlApi.Models;

public record TrafficRequest(string Scenario, int Vus, int DurationSeconds);

public record TrafficResult(string Scenario, long ExitCode, string Output);
