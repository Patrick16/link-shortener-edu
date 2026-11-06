namespace ControlApi.Models;

// Real outcome of a chaos probe - a request counted as failed on any non-2xx/3xx status OR a
// network-level exception (connection refused/reset/timeout against a genuinely stopped/degraded
// dependency), the same "status >= 200 && status < 400" success bar flow.js's own checks use.
public record ChaosProbeResult(int TotalRequests, int SuccessCount, int FailureCount, double ErrorRatePct, double P95Ms);
