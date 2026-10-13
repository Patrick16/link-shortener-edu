using ControlApi.Endpoints;
using ControlApi.Extensions;
using ControlApi.Hubs;
using ControlApi.Services;
using Docker.DotNet.Models;

var builder = WebApplication.CreateBuilder(args);

builder.AddConsoleLogging();
builder.AddControlPlaneServices();
builder.AddFrontendCors();

var app = builder.Build();

app.UseCors();

app.MapHub<StatusHub>("/hub/status");
app.MapCapabilityEndpoints();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

// Liveness ("/health" above) always answers, even if the Docker socket is unreachable - it only
// proves the ASP.NET process itself is up. Readiness checks the things control-api actually needs
// to do its job: the Docker socket every domain service shells out through, the pgcat.toml bind
// mount the pool-settings control rewrites, and the k6 image traffic runs pull on first use.
app.MapGet("/health/ready", async (IContainerRuntime runtime, IConfiguration configuration, CancellationToken ct) =>
{
    var checks = new Dictionary<string, string>();

    try
    {
        await runtime.Client.System.PingAsync(ct);
        checks["docker"] = "ok";
    }
    catch (Exception ex)
    {
        checks["docker"] = $"unreachable: {ex.Message}";
    }

    var pgcatTomlPath = Path.Combine(configuration["Pgcat:ConfigDir"] ?? "/pgcat-config", "pgcat.toml");
    checks["pgcat.toml"] = File.Exists(pgcatTomlPath) ? "ok" : $"missing at {pgcatTomlPath}";

    try
    {
        var k6Images = await runtime.Client.Images.ListImagesAsync(new ImagesListParameters
        {
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["reference"] = new Dictionary<string, bool> { [TrafficService.K6Image] = true },
            },
        }, ct);
        checks["k6-image"] = k6Images.Count > 0 ? "ok" : "not pulled yet (pulled on first traffic run)";
    }
    catch (Exception ex)
    {
        checks["k6-image"] = $"check failed: {ex.Message}";
    }

    // Only Docker gates readiness - a missing pgcat.toml or unpulled k6 image just means those
    // specific controls haven't been exercised yet, not that control-api itself is broken.
    var ready = checks["docker"] == "ok";
    return ready ? Results.Ok(checks) : Results.Json(checks, statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.MapContainerEndpoints();
app.MapInfraEndpoints();
app.MapTrafficEndpoints();
app.MapScenarioEndpoints();
app.MapPresetEndpoints();
app.MapRunHistoryEndpoints();
app.MapTraceEndpoints();

app.Run();
