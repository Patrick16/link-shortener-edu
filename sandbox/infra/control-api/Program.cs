using ControlApi.Hubs;
using ControlApi.Models;
using ControlApi.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IDockerService, DockerService>();
builder.Services.AddSignalR();
builder.Services.AddHostedService<StatusPollerService>();

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

app.MapGet("/api/traffic/scenarios", (IDockerService docker) => Results.Ok(docker.ListTrafficScenarios()));

app.MapPost("/api/traffic", async (TrafficRequest request, IDockerService docker, CancellationToken ct) =>
{
    if (request.Vus is < 1 or > 200)
    {
        return Results.BadRequest(new { error = "vus must be between 1 and 200" });
    }

    if (request.DurationSeconds is < 1 or > 120)
    {
        return Results.BadRequest(new { error = "durationSeconds must be between 1 and 120" });
    }

    var result = await docker.RunTrafficAsync(request, ct);
    return result is null ? Results.NotFound(new { error = $"unknown scenario '{request.Scenario}'" }) : Results.Ok(result);
});

app.Run();
