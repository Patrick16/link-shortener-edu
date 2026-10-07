using ControlApi.Models;
using ControlApi.Services;

namespace ControlApi.Endpoints;

// Lifecycle, chaos and scaling controls for the compose services control-api manages.
public static class ContainerEndpoints
{
    public static IEndpointRouteBuilder MapContainerEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/containers", async (IContainerLifecycleService lifecycle, CancellationToken ct) =>
            Results.Ok(await lifecycle.ListContainersAsync(ct)));

        app.MapPost("/api/containers/{serviceId}/stop", async (string serviceId, IContainerLifecycleService lifecycle, CancellationToken ct) =>
        {
            var result = await lifecycle.StopAsync(serviceId, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        app.MapPost("/api/containers/{serviceId}/start", async (string serviceId, IContainerLifecycleService lifecycle, CancellationToken ct) =>
        {
            var result = await lifecycle.StartAsync(serviceId, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        app.MapPost("/api/containers/{serviceId}/restart", async (string serviceId, IContainerLifecycleService lifecycle, CancellationToken ct) =>
        {
            var result = await lifecycle.RestartAsync(serviceId, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        app.MapPost("/api/containers/{serviceId}/degrade", async (string serviceId, ChaosRequest request, IChaosService chaos, CancellationToken ct) =>
        {
            if (request.DurationSeconds is < 1 or > 300)
            {
                return Results.BadRequest(new { error = "durationSeconds must be between 1 and 300" });
            }

            if (request.Type == ChaosType.Delay && request.Amount is < 1 or > 10_000)
            {
                return Results.BadRequest(new { error = "amount (delay ms) must be between 1 and 10000" });
            }

            if (request.Type == ChaosType.Loss && request.Amount is < 1 or > 100)
            {
                return Results.BadRequest(new { error = "amount (loss %) must be between 1 and 100" });
            }

            var result = await chaos.DegradeAsync(serviceId, request, ct);
            return result is null ? Results.NotFound() : Results.Ok(result);
        });

        app.MapPost("/api/containers/{serviceId}/heal", async (string serviceId, IChaosService chaos, CancellationToken ct) =>
            Results.Ok(new { stopped = await chaos.HealAsync(serviceId, ct) }));

        app.MapGet("/api/containers/{containerId}/stats/history", (string containerId, ResourceStatsStore store) =>
            Results.Ok(store.GetHistory(containerId)));

        app.MapGet("/api/containers/scalable", (IContainerLifecycleService lifecycle) => Results.Ok(lifecycle.ListScalableServices()));

        app.MapPost("/api/containers/{serviceId}/scale", async (string serviceId, ScaleRequest request, IContainerLifecycleService lifecycle, CancellationToken ct) =>
        {
            if (request.Replicas is < 1 or > 100)
            {
                return Results.BadRequest(new { error = "replicas must be between 1 and 100" });
            }

            var result = await lifecycle.ScaleAsync(serviceId, request.Replicas, ct);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        });

        return app;
    }
}
