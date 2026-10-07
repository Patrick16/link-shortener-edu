namespace ControlApi.Models;

// One decoded OTel span, flattened to just what BottleneckAdvisor needs - which service emitted
// it, what it was called (e.g. "POST /Links", "link.created publish", "link.created consume" -
// see MessagingActivitySource in the backend), and how long it took. ServiceId is the docker
// compose service name (resolved from the span's resource "service.name" attribute via
// TraceStore.ServiceNameToServiceId), not the raw OTel resource attribute, so it lines up with
// every other node-keyed stat this app already has (ResourceSample, ReplicaCount, ...).
public record TraceSpanRecord(string ServiceId, string SpanName, int? Kind, DateTimeOffset StartTime, double DurationMs);

// Aggregated span durations for one (service, span name) pair within a run's time window - the
// "which hop is slow" answer. Count lets the UI/advisor tell "one slow outlier" apart from
// "consistently slow".
public record TraceHopStats(string ServiceId, string SpanName, int Count, double AvgMs, double P95Ms, double MaxMs);

// Peak CPU/memory a node's container(s) reached during a run's time window - the resource-side
// counterpart to TraceHopStats. Captured live while the run is in flight (RunResourceMaxTracker),
// not read back afterwards, since ResourceStatsStore's own ring buffer only keeps the last 60s and
// a run can run longer than that. MaxMemoryPercent is computed against that same sample's own
// container memory limit, not a fixed byte threshold - a hardcoded byte cutoff would be meaningless
// across containers with different limits. MaxCpuPercent is the single busiest container's own
// peak; SumCpuPercent is the peak, across the run, of each poll tick's CPU summed over every
// replica of this service. For a scaled service (N containers sharing one ServiceId) that sum is
// the number that actually reflects the service's total load - MaxCpuPercent alone only ever looks
// at one replica at a time, so it silently misses saturation that's spread evenly across many
// (e.g. 20 replicas each at 100% raw looks "fine" by Max but is 2000% of a core combined). For a
// service that only ever runs one container, the two are equal.
public record NodeResourceMax(string ServiceId, double MaxCpuPercent, double SumCpuPercent, long MaxMemoryUsageBytes, double MaxMemoryPercent);
