using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ControlApi.Models;

namespace ControlApi.Services;

// Issues exactly one HTTP request to a real backend service and hands back the trace id it ran
// under, so a Lesson Mode lesson can link straight into Jaeger/Aspire Dashboard for that one
// specific request - TraceStore (see TraceEndpoints.cs) only ever aggregates spans for "which hop
// is slow" analysis, no single request is individually addressable there.
//
// Deliberately NOT a full OTel SDK dependency in control-api: this process only needs to mint a
// valid W3C traceparent header on its own outbound call. Every backend service already carries its
// own OpenTelemetry instrumentation (Shared/ServiceDefaults) and picks up an incoming traceparent
// as that trace's root automatically - the resulting trace is indistinguishable from one a real
// browser request would have produced, control-api just never exports a span of its own into it.
public class LessonTraceService(IHttpClientFactory httpClientFactory, IInfraToggleService infra) : ILessonTraceService
{
    // Mirrors TrafficService's own LINK_API_URL/REDIRECT_API_URL/AUTH_API_URL env-var mapping
    // exactly (see TrafficService.cs) - duplicated rather than shared because that one is baked
    // into k6 process env-var strings, not exposed as a reusable lookup.
    private static readonly IReadOnlyDictionary<string, string> DirectBaseUrls = new Dictionary<string, string>
    {
        ["auth-api"] = "http://auth-api:8080",
        ["link-api"] = "http://link-api:8080",
        ["redirect-api"] = "http://redirect-api:8080",
        ["reporting-api"] = "http://reporting-api:8080",
    };

    // Only link-api/redirect-api sit behind nginx - same two services TrafficService's own nginx-
    // bypass branch touches.
    private static readonly IReadOnlyDictionary<string, string> NginxRoutedBaseUrls = new Dictionary<string, string>
    {
        ["link-api"] = "http://nginx:8082",
        ["redirect-api"] = "http://nginx:8083",
    };

    public async Task<TracedRequestResult> RunAsync(TracedRequestInput input, CancellationToken ct)
    {
        var status = await infra.GetInfraStatusAsync(ct);
        var client = httpClientFactory.CreateClient();

        // Untraced on purpose - a setup call (e.g. "create a link") is scaffolding for the lesson,
        // not the request the lesson is actually about, so it shouldn't show up as the trace root
        // the learner is sent to look at.
        var path = input.Path;
        if (input.Setup is { } setup)
        {
            var setupUrl = $"{ResolveBaseUrl(setup.ServiceId, status.NginxBypassed)}{setup.Path}";
            using var setupRequest = new HttpRequestMessage(new HttpMethod(setup.Method), setupUrl);
            if (setup.Body is not null)
            {
                setupRequest.Content = new StringContent(setup.Body, Encoding.UTF8, "application/json");
            }

            using var setupResponse = await client.SendAsync(setupRequest, ct);
            var json = await setupResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            var value = json.GetProperty(setup.ExtractField).GetString();
            path = path.Replace("{{value}}", value);
        }

        var baseUrl = ResolveBaseUrl(input.ServiceId, status.NginxBypassed);

        // A fresh, random W3C trace context (version-traceId-parentId-flags) - ActivityTraceId/
        // ActivitySpanId are part of System.Diagnostics (no extra package needed) and already
        // generate the correct hex width (32/16 chars) per the spec. flags=01 (sampled) so the
        // receiving service's own sampler never drops it.
        var traceId = ActivityTraceId.CreateRandom();
        var spanId = ActivitySpanId.CreateRandom();

        using var request = new HttpRequestMessage(new HttpMethod(input.Method), $"{baseUrl}{path}");
        request.Headers.Add("traceparent", $"00-{traceId}-{spanId}-01");

        var stopwatch = Stopwatch.StartNew();
        using var response = await client.SendAsync(request, ct);
        stopwatch.Stop();

        return new TracedRequestResult((int)response.StatusCode, stopwatch.Elapsed.TotalMilliseconds, traceId.ToHexString());
    }

    private static string ResolveBaseUrl(string serviceId, bool nginxBypassed) =>
        !nginxBypassed && NginxRoutedBaseUrls.TryGetValue(serviceId, out var routed)
            ? routed
            : DirectBaseUrls.GetValueOrDefault(serviceId)
                ?? throw new ArgumentOutOfRangeException(nameof(serviceId), serviceId, "Unknown serviceId for a traced request.");
}
