using ControlApi.Models;
using ControlApi.Services;

namespace ControlApi.Endpoints;

// Lesson Mode's own backend surface - the load-test trigger kind reuses the existing traffic-run
// endpoint exactly as it already is, and chaos's stop/degrade/heal actions reuse the existing
// generic docker-control endpoints, but a chaos lesson whose point is request-level behavior (not
// container reachability - see ChaosProbeInput's own comment) needs this traced-request/
// chaos-probe surface to actually exercise that during its own outage window.
public static class LessonEndpoints
{
    public static IEndpointRouteBuilder MapLessonEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/lessons/traced-request", async (TracedRequestInput input, ILessonTraceService trace, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await trace.RunAsync(input, ct));
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapPost("/api/lessons/chaos-probe", async (ChaosProbeInput input, ILessonProbeService probe, CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await probe.RunAsync(input, ct));
            }
            catch (ArgumentOutOfRangeException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        return app;
    }
}
