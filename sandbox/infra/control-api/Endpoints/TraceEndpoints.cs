using ControlApi.Models;
using ControlApi.Services;

namespace ControlApi.Endpoints;

public static class TraceEndpoints
{
    // otel-collector's own target for the traces pipeline (see otel-collector-config.yaml) - a plain
    // OTLP/JSON POST, not gRPC. Deserializes the body manually (instead of a typed minimal-API
    // parameter) so a shape this app's trimmed-down OtlpExportTraceServiceRequest doesn't expect
    // can never turn into an automatic 400 from the framework's own model binding, which would bypass
    // the try/catch entirely and make otel-collector retry-storm this endpoint. Always 202s: losing a
    // batch of spans only degrades the bottleneck advisor, it never breaks a run.
    public static IEndpointRouteBuilder MapTraceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/traces/ingest", async (HttpRequest httpRequest, TraceStore traceStore, ILogger<Program> logger) =>
        {
            try
            {
                var request = await httpRequest.ReadFromJsonAsync<OtlpExportTraceServiceRequest>();
                if (request is not null)
                {
                    traceStore.Ingest(request);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to ingest a trace batch - dropping it");
            }

            return Results.Accepted();
        });

        return app;
    }
}
