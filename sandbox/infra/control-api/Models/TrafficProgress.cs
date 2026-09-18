namespace ControlApi.Models;

public record TrafficProgress(int ElapsedSeconds, int TotalSeconds, int PercentComplete, long IterationsSoFar, double IterationsPerSecond);
