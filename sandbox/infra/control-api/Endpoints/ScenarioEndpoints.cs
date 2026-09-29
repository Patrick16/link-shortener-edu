using ControlApi.Models;
using ControlApi.Services;

namespace ControlApi.Endpoints;

// Saved custom traffic scenarios, plus the endpoint/data-source catalogs they're built from.
public static class ScenarioEndpoints
{
    public static IEndpointRouteBuilder MapScenarioEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/endpoints", (IDockerService docker) => Results.Ok(docker.ListKnownEndpoints()));

        app.MapGet("/api/data-sources", (IDockerService docker) => Results.Ok(docker.ListDataSources()));

        app.MapGet("/api/scenarios", async (IScenarioStore store, CancellationToken ct) => Results.Ok(await store.ListAsync(ct)));

        app.MapPost("/api/scenarios", async (CustomScenario scenario, IScenarioStore store, IDockerService docker, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(scenario.Name))
            {
                return Results.BadRequest(new { error = "name is required" });
            }

            var knownIds = docker.ListKnownEndpoints().Select(ep => ep.Id).ToList();
            var unknown = scenario.Steps.Where(s => !knownIds.Contains(s.EndpointId)).Select(s => s.EndpointId).ToList();
            if (scenario.Steps.Count == 0 || unknown.Count > 0)
            {
                return Results.BadRequest(new { error = $"steps must be a non-empty sequence of endpoints drawn from: {string.Join(", ", knownIds)}" });
            }

            if (scenario.Mode == "iterations")
            {
                if (scenario.Vus < 1 || scenario.Iterations < 1)
                {
                    return Results.BadRequest(new { error = "vus and iterations must be at least 1 in iterations mode" });
                }
            }
            else if (scenario.Points.Count < 2)
            {
                return Results.BadRequest(new { error = "at least 2 points are required" });
            }

            await store.SaveAsync(scenario, ct);
            return Results.Ok(scenario);
        });

        app.MapDelete("/api/scenarios/{name}", async (string name, IScenarioStore store, CancellationToken ct) =>
            await store.DeleteAsync(name, ct) ? Results.Ok() : Results.NotFound());

        return app;
    }
}
