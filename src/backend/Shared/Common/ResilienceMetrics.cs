using System.Diagnostics.Metrics;

namespace Common;

// Manual metric for circuit-breaker state transitions - mirrors Infrastructure/
// MessagingActivitySource.cs's own pattern for tracing (a hand-rolled source registered once with
// the OTel provider, since there's no auto-instrumentation for this). Without it, a breaker that
// opens "silently heals" the request path - every call just starts failing fast instead of timing
// out, with nothing anywhere saying a dependency is currently being bypassed. Registered with the
// OTel MeterProvider in Shared/ServiceDefaults/Extensions.cs.
public static class ResilienceMetrics
{
    public const string MeterName = "LinkShortener.Resilience";
    private static readonly Meter Meter = new(MeterName);

    // Tags on every increment: "dependency" (redis/grpc/rabbitmq/geo-ip) and "state"
    // (opened/closed/half-opened) - one counter, sliced by both tags, rather than one gauge per
    // dependency that would need inventing a new metric name every time a new dependency gets a
    // breaker.
    public static readonly Counter<long> CircuitBreakerStateTransitions = Meter.CreateCounter<long>(
        "resilience.circuit_breaker.transitions",
        unit: "{transition}",
        description: "Counts each time a circuit breaker opens, closes, or half-opens, tagged by dependency and state.");
}
