using ControlApi.Models;
using ControlApi.Services;

namespace ControlApi.Endpoints;

// Saved infra config bundles ("presets") - persistence only. Capturing the current config,
// diffing it against a preset, and applying a preset all happen client-side against the GET/POST
// routes CapabilityEndpoints/InfraEndpoints/ContainerEndpoints already expose; this endpoint set
// is just CRUD over the name.
public static class PresetEndpoints
{
    public static IEndpointRouteBuilder MapPresetEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/presets", async (IPresetStore store, CancellationToken ct) => Results.Ok(await store.ListAsync(ct)));

        app.MapPost("/api/presets", async (Preset preset, IPresetStore store, IScenarioStore scenarios, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(preset.Name))
            {
                return Results.BadRequest(new { error = "name is required" });
            }

            if (preset.ScenarioName is not null)
            {
                var known = await scenarios.ListAsync(ct);
                if (!known.Any(s => s.Name == preset.ScenarioName))
                {
                    return Results.BadRequest(new { error = $"no saved scenario named '{preset.ScenarioName}'" });
                }
            }

            await store.SaveAsync(preset, ct);
            return Results.Ok(preset);
        });

        app.MapDelete("/api/presets/{name}", async (string name, IPresetStore store, CancellationToken ct) =>
            await store.DeleteAsync(name, ct) ? Results.Ok() : Results.NotFound());

        return app;
    }
}
