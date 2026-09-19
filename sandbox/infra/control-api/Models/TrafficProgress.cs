namespace ControlApi.Models;

// TargetIterations is only set for an iteration-count run (TrafficRequest.Iterations) - the UI uses
// its presence to show "X/Y iterations" instead of "Xs/Ys", since there's no fixed total duration
// to measure against in that mode (PercentComplete is iterationsSoFar/TargetIterations there instead
// of elapsed/TotalSeconds).
//
// Phase is "preparing" for the brief window (if any) where a requested DataPool is being fetched
// from the real app before k6 even starts - PreparedCount/PreparedTarget carry that fetch's own
// progress then (PercentComplete mirrors PreparedCount/PreparedTarget so the UI can reuse the same
// progress bar). Every other field is meaningless during that phase. Once k6 actually starts, every
// push goes back to Phase "running" with these two left null, same as before DataPool existed.
public record TrafficProgress(
    int ElapsedSeconds,
    int TotalSeconds,
    int PercentComplete,
    int ActiveVus,
    long IterationsSoFar,
    double IterationsPerSecond,
    int? TargetIterations = null,
    string Phase = "running",
    int? PreparedCount = null,
    int? PreparedTarget = null);
