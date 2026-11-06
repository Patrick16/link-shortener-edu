using ControlApi.Models;
using ControlApi.Services;

namespace ControlApi.Endpoints;

// Lesson Mode's one piece of new backend surface - every other trigger kind (load-test, chaos)
// reuses the existing traffic-run and generic docker-control endpoints exactly as they already
// are (see .notes/PLAN.md's "Feature: Lesson Mode" entry).
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

        return app;
    }
}
