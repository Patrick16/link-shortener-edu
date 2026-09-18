namespace ControlApi.Models;

public record ResourceSample(string ServiceId, double CpuPercent, long MemoryUsageBytes, long MemoryLimitBytes, DateTimeOffset Timestamp);
