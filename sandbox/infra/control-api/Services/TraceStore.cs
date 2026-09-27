using System.Collections.Concurrent;
using System.Globalization;
using ControlApi.Models;

namespace ControlApi.Services;

// Receives the OTLP/JSON copy of every span otel-collector forwards (see
// otel-collector-config.yaml and Program.cs's /api/traces/ingest endpoint), decodes it into
// TraceSpanRecords, and keeps a short rolling window of them in memory - long enough to cover a
// traffic run (runs are capped at 120s / 600s for a custom ramp, see Program.cs's traffic
// validation), not a real tracing backend. Evicted on a timer rather than per-insert, since spans
// arrive in export-batch bursts, not one at a time.
public class TraceStore
{
    // OTel's own resource attribute (builder.Environment.ApplicationName in
    // Shared/ServiceDefaults/Extensions.cs) is the .NET project name, not the docker-compose
    // service id everything else in this app is keyed by - map the 5 traced services explicitly
    // rather than guessing a PascalCase-to-kebab-case conversion.
    private static readonly IReadOnlyDictionary<string, string> ServiceNameToServiceId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["AuthApi"] = "auth-api",
        ["LinkApi"] = "link-api",
        ["RedirectApi"] = "redirect-api",
        ["ShortenerService"] = "shortener-service",
        ["TrafficService"] = "traffic-service",
    };

    private static readonly TimeSpan RetentionWindow = TimeSpan.FromMinutes(15);

    private readonly ConcurrentQueue<TraceSpanRecord> _spans = new();

    public void Ingest(OtlpExportTraceServiceRequest request)
    {
        foreach (var resourceSpans in request.ResourceSpans ?? [])
        {
            var serviceName = resourceSpans.Resource?.Attributes?
                .FirstOrDefault(a => a.Key == "service.name")?.Value?.StringValue;

            if (serviceName is null || !ServiceNameToServiceId.TryGetValue(serviceName, out var serviceId))
            {
                continue; // Not one of our 5 traced services (or missing service.name) - nothing to attribute this to.
            }

            foreach (var scopeSpans in resourceSpans.ScopeSpans ?? [])
            {
                foreach (var span in scopeSpans.Spans ?? [])
                {
                    if (!TryParseUnixNano(span.StartTimeUnixNano, out var startNano) ||
                        !TryParseUnixNano(span.EndTimeUnixNano, out var endNano))
                    {
                        continue;
                    }

                    var start = DateTimeOffset.FromUnixTimeMilliseconds(startNano / 1_000_000);
                    var durationMs = (endNano - startNano) / 1_000_000.0;
                    _spans.Enqueue(new TraceSpanRecord(serviceId, span.Name ?? "(unnamed)", span.Kind, start, durationMs));
                }
            }
        }

        EvictOld();
    }

    public IReadOnlyList<TraceSpanRecord> GetSpansBetween(DateTimeOffset start, DateTimeOffset end)
    {
        return _spans.Where(s => s.StartTime >= start && s.StartTime <= end).ToList();
    }

    // Grouped by (service, span name) - e.g. "link-api"/"POST /Links" and "shortener-service"/
    // "link.created consume" are two separate hops, never merged, since they're two different
    // legs of the same request's lifecycle.
    public IReadOnlyList<TraceHopStats> GetHopStatsBetween(DateTimeOffset start, DateTimeOffset end)
    {
        return GetSpansBetween(start, end)
            .GroupBy(s => (s.ServiceId, s.SpanName))
            .Select(g =>
            {
                var durations = g.Select(s => s.DurationMs).OrderBy(d => d).ToList();
                var p95Index = Math.Min(durations.Count - 1, (int)Math.Ceiling(durations.Count * 0.95) - 1);
                return new TraceHopStats(
                    g.Key.ServiceId,
                    g.Key.SpanName,
                    durations.Count,
                    durations.Average(),
                    durations[Math.Max(0, p95Index)],
                    durations[^1]);
            })
            .ToList();
    }

    private void EvictOld()
    {
        var cutoff = DateTimeOffset.UtcNow - RetentionWindow;

        // A plain peek-and-stop-at-head scan assumed enqueue order is non-decreasing in StartTime,
        // but enqueue order is actually *arrival* order at /api/traces/ingest - concurrent POSTs
        // from the 5 independently-batching traced services can enqueue an older span after a
        // newer one (a batch that was briefly slow to export, a GC pause, otel-collector's own
        // batching delay...). Once a stale span ends up behind any younger one in the queue, a
        // head-only scan can never reach it again - it sits in _spans indefinitely. Draining the
        // whole queue and re-enqueueing only what's still within the window is O(n), but n is
        // bounded by RetentionWindow's span volume, not the store's whole lifetime - a small, known
        // cost for actually bounding memory instead of an ordering assumption that doesn't hold
        // under real concurrent ingestion. A concurrent Ingest() enqueuing mid-drain can have its
        // new span picked up by this same drain and simply requeued at the end (harmless reordering,
        // not data loss) - ConcurrentQueue's own Try* methods are individually thread-safe either way.
        var stillLive = new List<TraceSpanRecord>();
        while (_spans.TryDequeue(out var span))
        {
            if (span.StartTime >= cutoff)
            {
                stillLive.Add(span);
            }
        }

        foreach (var span in stillLive)
        {
            _spans.Enqueue(span);
        }
    }

    private static bool TryParseUnixNano(string? value, out long nanos)
    {
        // OTLP/JSON encodes fixed64 fields (these timestamps) as strings to avoid precision loss -
        // never a bare JSON number.
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out nanos);
    }
}
