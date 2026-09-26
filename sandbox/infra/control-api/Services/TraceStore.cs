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
        while (_spans.TryPeek(out var oldest) && oldest.StartTime < cutoff)
        {
            _spans.TryDequeue(out _);
        }
    }

    private static bool TryParseUnixNano(string? value, out long nanos)
    {
        // OTLP/JSON encodes fixed64 fields (these timestamps) as strings to avoid precision loss -
        // never a bare JSON number.
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out nanos);
    }
}
