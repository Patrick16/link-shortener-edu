using System.Text.Json.Serialization;

namespace ControlApi.Models;

// Minimal POCOs for the OTLP/JSON trace export payload (ExportTraceServiceRequest) that
// otel-collector's otlphttp exporter produces when configured with `encoding: json` (see
// otel-collector-config.yaml). This is the standard OTLP JSON protobuf mapping - fixed64 fields
// (the nanosecond timestamps) are encoded as JSON strings, not numbers, to avoid precision loss.
// Every property carries an explicit JsonPropertyName rather than relying on whatever global
// camelCase policy control-api's own minimal API happens to use elsewhere - this payload's casing
// is dictated by the OTLP spec, not by us. Only the fields BottleneckAdvisor actually needs are
// modeled; anything else in a real OTLP payload is silently ignored by System.Text.Json rather
// than rejected.
public record OtlpExportTraceServiceRequest(
    [property: JsonPropertyName("resourceSpans")] List<OtlpResourceSpans>? ResourceSpans);

public record OtlpResourceSpans(
    [property: JsonPropertyName("resource")] OtlpResource? Resource,
    [property: JsonPropertyName("scopeSpans")] List<OtlpScopeSpans>? ScopeSpans);

public record OtlpResource(
    [property: JsonPropertyName("attributes")] List<OtlpKeyValue>? Attributes);

public record OtlpScopeSpans(
    [property: JsonPropertyName("spans")] List<OtlpSpan>? Spans);

// Confirmed against a real payload from otel-collector 0.161.0: `kind` is a plain JSON number
// (the proto enum's numeric value), not its string name.
public record OtlpSpan(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("kind")] int? Kind,
    [property: JsonPropertyName("startTimeUnixNano")] string? StartTimeUnixNano,
    [property: JsonPropertyName("endTimeUnixNano")] string? EndTimeUnixNano);

public record OtlpKeyValue(
    [property: JsonPropertyName("key")] string? Key,
    [property: JsonPropertyName("value")] OtlpAnyValue? Value);

public record OtlpAnyValue(
    [property: JsonPropertyName("stringValue")] string? StringValue);
