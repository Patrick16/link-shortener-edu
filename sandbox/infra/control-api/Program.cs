using ControlApi.Endpoints;
using ControlApi.Extensions;
using ControlApi.Hubs;

var builder = WebApplication.CreateBuilder(args);

builder.AddConsoleLogging();
builder.AddControlPlaneServices();
builder.AddFrontendCors();

var app = builder.Build();

app.UseCors();

app.MapHub<StatusHub>("/hub/status");
app.MapCapabilityEndpoints();

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

app.MapContainerEndpoints();
app.MapInfraEndpoints();
app.MapTrafficEndpoints();
app.MapScenarioEndpoints();
app.MapPresetEndpoints();
app.MapRunHistoryEndpoints();
app.MapTraceEndpoints();

app.Run();
