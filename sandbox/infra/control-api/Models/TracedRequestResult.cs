namespace ControlApi.Models;

public record TracedRequestResult(int StatusCode, double LatencyMs, string TraceId);
