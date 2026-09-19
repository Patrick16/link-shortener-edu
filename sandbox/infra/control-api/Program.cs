using ControlApi.Hubs;
using ControlApi.Models;
using ControlApi.Services;
using Microsoft.AspNetCore.SignalR;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IDockerService, DockerService>();
builder.Services.AddSingleton<IScenarioStore, ScenarioStore>();
builder.Services.AddSingleton<ResourceStatsStore>();
builder.Services.AddSignalR();
builder.Services.AddHostedService<StatusPollerService>();
builder.Services.AddHostedService<ResourceStatsPollerService>();

var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? ["http://localhost:5173", "http://localhost:5174"];
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins(corsOrigins).AllowAnyMethod().AllowAnyHeader().AllowCredentials());
});

var app = builder.Build();

app.UseCors();

app.MapHub<StatusHub>("/hub/status");

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.MapGet("/api/containers", async (IDockerService docker, CancellationToken ct) =>
    Results.Ok(await docker.ListContainersAsync(ct)));

app.MapPost("/api/containers/{serviceId}/stop", async (string serviceId, IDockerService docker, CancellationToken ct) =>
{
    var result = await docker.StopAsync(serviceId, ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
});

app.MapPost("/api/containers/{serviceId}/start", async (string serviceId, IDockerService docker, CancellationToken ct) =>
{
    var result = await docker.StartAsync(serviceId, ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
});

app.MapPost("/api/containers/{serviceId}/restart", async (string serviceId, IDockerService docker, CancellationToken ct) =>
{
    var result = await docker.RestartAsync(serviceId, ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
});

app.MapPost("/api/containers/{serviceId}/degrade", async (string serviceId, ChaosRequest request, IDockerService docker, CancellationToken ct) =>
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

    var result = await docker.DegradeAsync(serviceId, request, ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
});

app.MapPost("/api/containers/{serviceId}/heal", async (string serviceId, IDockerService docker, CancellationToken ct) =>
    Results.Ok(new { stopped = await docker.HealAsync(serviceId, ct) }));

app.MapGet("/api/containers/{serviceId}/stats/history", (string serviceId, ResourceStatsStore store) =>
    Results.Ok(store.GetHistory(serviceId)));

app.MapGet("/api/containers/scalable", (IDockerService docker) => Results.Ok(docker.ListScalableServices()));

app.MapPost("/api/containers/redis/flush-cache", async (IDockerService docker, CancellationToken ct) =>
{
    var result = await docker.FlushRedisAsync(ct);
    return result is null ? Results.NotFound() : Results.Ok(new { flushed = result });
});

app.MapPost("/api/containers/{serviceId}/scale", async (string serviceId, ScaleRequest request, IDockerService docker, CancellationToken ct) =>
{
    if (request.Replicas is < 1 or > 10)
    {
        return Results.BadRequest(new { error = "replicas must be between 1 and 10" });
    }

    var result = await docker.ScaleAsync(serviceId, request.Replicas, ct);
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapPost("/api/traffic", (TrafficRequest request, IDockerService docker, IHubContext<StatusHub> hub, ILogger<Program> logger) =>
{
    if (request.Steps.Count == 0)
    {
        return Results.BadRequest(new { error = "at least one endpoint step is required" });
    }

    var unknownEndpoints = request.Steps.Where(s => !docker.ListKnownEndpoints().Any(ep => ep.Id == s.EndpointId)).Select(s => s.EndpointId).ToList();
    if (unknownEndpoints.Count > 0)
    {
        return Results.BadRequest(new { error = $"unknown endpoint(s): {string.Join(", ", unknownEndpoints)}" });
    }

    if (request.Steps.Any(s => s.PauseAfterSeconds is < 0 or > 30))
    {
        return Results.BadRequest(new { error = "each step's pauseAfterSeconds must be between 0 and 30" });
    }

    if (request.Iterations is { } iterations)
    {
        // Iteration-count runs use k6's shared-iterations executor - flat VUs, no ramp, so none of
        // the Stages/DurationSeconds checks below apply.
        if (request.Vus < 1)
        {
            return Results.BadRequest(new { error = "vus must be at least 1 for an iteration-count run" });
        }

        if (iterations is < 1 or > 100_000)
        {
            return Results.BadRequest(new { error = "iterations must be between 1 and 100000" });
        }
    }
    else
    {
        // With a custom ramp, Vus is just the starting point k6 ramps from - 0 is exactly what a spike
        // profile (or any "ramp up from idle") wants there. Only the flat constant-VUs run needs it to
        // be at least 1, since there it's the VU count for the entire run.
        if (request.Vus < 0 || (request.Stages is not { Count: > 0 } && request.Vus < 1))
        {
            return Results.BadRequest(new { error = "vus must be at least 1 (or at least 0 as a ramp's starting point)" });
        }

        if (request.Stages is { Count: > 0 } stages)
        {
            if (stages.Any(s => s.DurationSeconds < 1))
            {
                return Results.BadRequest(new { error = "each stage's durationSeconds must be at least 1" });
            }

            if (stages.Any(s => s.TargetVus < 0))
            {
                return Results.BadRequest(new { error = "a stage's targetVus can't be negative" });
            }

            // A custom ramp is user-drawn, so it isn't bound by the flat run's 120s cap - just a
            // generous ceiling so a slipped point on the graph can't lock up a k6 container forever.
            if (stages.Sum(s => s.DurationSeconds) > 600)
            {
                return Results.BadRequest(new { error = "total stage duration can't exceed 600 seconds" });
            }
        }
        else if (request.DurationSeconds is < 1 or > 120)
        {
            return Results.BadRequest(new { error = "durationSeconds must be between 1 and 120" });
        }
    }

    // Runs in the background and reports over SignalR (trafficProgress while it runs,
    // trafficCompleted with the final TrafficReport) instead of blocking the HTTP request for the
    // full duration - lets the UI show a live progress bar instead of a frozen spinner.
    _ = Task.Run(async () =>
    {
        try
        {
            var report = await docker.RunTrafficAsync(
                request,
                progress => hub.Clients.All.SendAsync("trafficProgress", progress),
                CancellationToken.None);

            if (report is not null)
            {
                await hub.Clients.All.SendAsync("trafficCompleted", report);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Traffic run for {Scenario} failed", request.Scenario);
            await hub.Clients.All.SendAsync("trafficFailed", new { request.Scenario, error = ex.Message });
        }
    });

    return Results.Accepted();
});

app.MapGet("/api/endpoints", (IDockerService docker) => Results.Ok(docker.ListKnownEndpoints()));

app.MapGet("/api/infra/status", (IDockerService docker) => Results.Ok(docker.GetInfraStatus()));

// "Enabled" is framed the same way for all three - true is the normal/default state, false is the
// degraded one being demonstrated - even though nginx's own field name (NginxBypassed) is the
// inverse of that, since bypassing is the interesting state worth naming directly there.
app.MapPost("/api/infra/nginx", (InfraToggleRequest request, IDockerService docker) =>
    Results.Ok(docker.SetNginxBypass(!request.Enabled)));

app.MapPost("/api/infra/pgcat", async (InfraToggleRequest request, IDockerService docker, ILogger<Program> logger, CancellationToken ct) =>
{
    try
    {
        return Results.Ok(await docker.SetPgcatEnabledAsync(request.Enabled, ct));
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Toggling pgcat to {Enabled} failed", request.Enabled);
        return Results.Problem(ex.Message);
    }
});

app.MapPost("/api/infra/cache", async (InfraToggleRequest request, IDockerService docker, ILogger<Program> logger, CancellationToken ct) =>
{
    try
    {
        return Results.Ok(await docker.SetCacheEnabledAsync(request.Enabled, ct));
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Toggling cache to {Enabled} failed", request.Enabled);
        return Results.Problem(ex.Message);
    }
});

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

app.Run();
