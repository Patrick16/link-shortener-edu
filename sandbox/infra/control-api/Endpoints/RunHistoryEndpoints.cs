using ControlApi.Models;
using ControlApi.Services;

namespace ControlApi.Endpoints;

// Saved snapshots of finished traffic runs.
public static class RunHistoryEndpoints
{
    public static IEndpointRouteBuilder MapRunHistoryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/runs", async (IRunHistoryStore store, CancellationToken ct) => Results.Ok(await store.ListAsync(ct)));

        app.MapGet("/api/runs/{id}", async (string id, IRunHistoryStore store, CancellationToken ct) =>
        {
            var snapshot = await store.GetAsync(id, ct);
            return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
        });

        app.MapDelete("/api/runs", async (IRunHistoryStore store, CancellationToken ct) =>
        {
            var failedCount = await store.ClearAsync(ct);
            return Results.Ok(new { failedCount });
        });

        app.MapDelete("/api/runs/{id}", async (string id, IRunHistoryStore store, CancellationToken ct) =>
            await store.DeleteAsync(id, ct) ? Results.Ok() : Results.NotFound());

        app.MapPost("/api/runs/{id}/name", async (string id, RenameRunRequest request, IRunHistoryStore store, CancellationToken ct) =>
        {
            var scenario = request.Scenario?.Trim();
            if (string.IsNullOrEmpty(scenario))
            {
                return Results.BadRequest(new { error = "scenario name cannot be empty" });
            }

            var summary = await store.RenameAsync(id, scenario, ct);
            return summary is null ? Results.NotFound() : Results.Ok(summary);
        });

        return app;
    }
}
