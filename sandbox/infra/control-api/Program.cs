using ControlApi.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IDockerService, DockerService>();

var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? ["http://localhost:5173", "http://localhost:5174"];
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins(corsOrigins).AllowAnyMethod().AllowAnyHeader());
});

var app = builder.Build();

app.UseCors();

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

app.Run();
