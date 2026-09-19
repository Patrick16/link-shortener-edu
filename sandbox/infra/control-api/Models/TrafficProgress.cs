namespace ControlApi.Models;

// TargetIterations is only set for an iteration-count run (TrafficRequest.Iterations) - the UI uses
// its presence to show "X/Y iterations" instead of "Xs/Ys", since there's no fixed total duration
// to measure against in that mode (PercentComplete is iterationsSoFar/TargetIterations there instead
// of elapsed/TotalSeconds).
public record TrafficProgress(
    int ElapsedSeconds,
    int TotalSeconds,
    int PercentComplete,
    int ActiveVus,
    long IterationsSoFar,
    double IterationsPerSecond,
    int? TargetIterations = null);
