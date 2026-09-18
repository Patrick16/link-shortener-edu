namespace ControlApi.Models;

public record TrafficProgress(int ElapsedSeconds, int TotalSeconds, int PercentComplete, int ActiveVus, long IterationsSoFar, double IterationsPerSecond);
