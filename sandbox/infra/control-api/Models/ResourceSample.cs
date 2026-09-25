namespace ControlApi.Models;

// ContainerId/ContainerNumber identify which specific replica this sample came from - a scaled
// service (N containers sharing one ServiceId) gets one ResourceSample per running container, not
// one shared/aggregated sample, so the UI can show each instance's own reading instead of silently
// picking one.
public record ResourceSample(
    string ServiceId,
    string ContainerId,
    int ContainerNumber,
    double CpuPercent,
    long MemoryUsageBytes,
    long MemoryLimitBytes,
    int TcpConnections,
    DateTimeOffset Timestamp);
