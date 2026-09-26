using ControlApi.Hubs;
using ControlApi.Models;
using ControlApi.Services;
using ControlApi.Services.Capabilities;
using Microsoft.AspNetCore.SignalR;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IDockerService, DockerService>();
builder.Services.AddSingleton<IScenarioStore, ScenarioStore>();
builder.Services.AddSingleton<IRunHistoryStore, RunHistoryStore>();
builder.Services.AddSingleton<ResourceStatsStore>();
builder.Services.AddSingleton<TraceStore>();
builder.Services.AddSingleton<RunResourceMaxTracker>();
builder.Services.AddSignalR();
builder.Services.AddHostedService<StatusPollerService>();
builder.Services.AddHostedService<ResourceStatsPollerService>();
builder.Services.AddHostedService<TopologyPollerService>();

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

// Component-specific infra endpoints (pgcat pool, Sentinel config, etc.) are registered per
// capability class rather than mapped unconditionally here - see CapabilityFactory for how the set
// actually mapped is driven by which capability strings architecture.json's nodes reference.
var architectureFile = builder.Configuration["Architecture:File"] ?? "/workspace/frontend/architecture-map/src/data/architecture.json";
try
{
    var capabilityFactory = new CapabilityFactory();
    var capabilities = capabilityFactory.BuildFromArchitectureFile(
        architectureFile,
        app.Services.GetRequiredService<IDockerService>(),
        app.Services.GetRequiredService<ILoggerFactory>());
    foreach (var capability in capabilities)
    {
        capability.MapEndpoints(app);
    }
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "Failed to load capabilities from architecture file {Path} - component-specific infra endpoints will be unavailable", architectureFile);
}

app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));

// otel-collector's own target for the traces pipeline (see otel-collector-config.yaml) - a plain
// OTLP/JSON POST, not gRPC. Deserializes the body manually (instead of a typed minimal-API
// parameter) so a shape this app's trimmed-down OtlpExportTraceServiceRequest doesn't expect
// can never turn into an automatic 400 from the framework's own model binding, which would bypass
// the try/catch entirely and make otel-collector retry-storm this endpoint. Always 202s: losing a
// batch of spans only degrades the bottleneck advisor, it never breaks a run.
app.MapPost("/api/traces/ingest", async (HttpRequest httpRequest, TraceStore traceStore) =>
{
    try
    {
        var request = await httpRequest.ReadFromJsonAsync<OtlpExportTraceServiceRequest>();
        if (request is not null)
        {
            traceStore.Ingest(request);
        }
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Failed to ingest a trace batch - dropping it");
    }

    return Results.Accepted();
});

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

app.MapGet("/api/containers/{containerId}/stats/history", (string containerId, ResourceStatsStore store) =>
    Results.Ok(store.GetHistory(containerId)));

app.MapGet("/api/containers/scalable", (IDockerService docker) => Results.Ok(docker.ListScalableServices()));

app.MapPost("/api/containers/{serviceId}/scale", async (string serviceId, ScaleRequest request, IDockerService docker, CancellationToken ct) =>
{
    if (request.Replicas is < 1 or > 100)
    {
        return Results.BadRequest(new { error = "replicas must be between 1 and 100" });
    }

    var result = await docker.ScaleAsync(serviceId, request.Replicas, ct);
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

// Guards against two overlapping /api/traffic runs. Nothing enforced this before
// RunResourceMaxTracker existed either, but that tracker is a singleton assuming one run at a time
// (see its own comment) - a second concurrent run would silently corrupt both runs' resource maxima
// (e.g. run 2's BeginRun() clearing state run 1 is still accumulating into). 0 = idle, 1 = running.
var trafficRunActive = 0;

app.MapPost("/api/traffic", (TrafficRequest request, IDockerService docker, IRunHistoryStore runHistory, IHubContext<StatusHub> hub, ILogger<Program> logger, TraceStore traceStore, RunResourceMaxTracker resourceMaxTracker) =>
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

    if (request.DataPool is { } dataPool)
    {
        if (!docker.ListDataSources().Any(s => s.Id == dataPool.SourceId))
        {
            return Results.BadRequest(new { error = $"unknown data source: {dataPool.SourceId}" });
        }

        if (dataPool.Count is < 1 or > 20_000)
        {
            return Results.BadRequest(new { error = "dataPool.count must be between 1 and 20000" });
        }

        if (dataPool.Mode is not ("sequential" or "random"))
        {
            return Results.BadRequest(new { error = "dataPool.mode must be 'sequential' or 'random'" });
        }
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

    // Only acquired once every validation check above has already passed - a rejected request
    // never claims the slot, so it can't block a real run from starting right after.
    if (Interlocked.CompareExchange(ref trafficRunActive, 1, 0) != 0)
    {
        return Results.Conflict(new { error = "a traffic run is already in progress" });
    }

    // Runs in the background and reports over SignalR (trafficProgress while it runs,
    // trafficCompleted with the final TrafficReport) instead of blocking the HTTP request for the
    // full duration - lets the UI show a live progress bar instead of a frozen spinner.
    _ = Task.Run(async () =>
    {
        try
        {
            // Window used to correlate trace spans and resource-usage peaks with this exact run -
            // captured around the k6 call itself, not the whole HTTP request handler, so it doesn't
            // pick up anything from validation or a previous run's tail.
            var runStart = DateTimeOffset.UtcNow;
            resourceMaxTracker.BeginRun();

            var report = await docker.RunTrafficAsync(
                request,
                progress => hub.Clients.All.SendAsync("trafficProgress", progress),
                CancellationToken.None);

            var runEnd = DateTimeOffset.UtcNow;
            // Always ends tracking, even if the run failed/returned null - otherwise a failed run
            // would leave the tracker stuck "on" and silently accumulate maxima into whatever the
            // next run turns out to be.
            var resourceMaxima = resourceMaxTracker.EndRun();

            if (report is not null)
            {
                await hub.Clients.All.SendAsync("trafficCompleted", report);

                // Best-effort - a snapshot failing to save shouldn't hide the report the user is
                // already looking at. Captured right after the run so replica counts/connections
                // reflect the state the load was actually generated against, not some later moment.
                try
                {
                    var containers = await docker.ListContainersAsync(CancellationToken.None);
                    var replicas = containers
                        .GroupBy(c => c.ServiceId)
                        .Select(g => new ReplicaCount(g.Key, g.Count(c => c.State == "running")))
                        .ToList();

                    // Best-effort, same reasoning as the snapshot as a whole - none of these should
                    // hide the report if a given control's live read fails for some reason.
                    var replicationLags = new List<ReplicationLagEntry>();
                    foreach (var replicaId in new[] { "postgres-replica1", "postgres-replica2" })
                    {
                        try
                        {
                            var lag = await docker.GetReplicationLagAsync(replicaId, CancellationToken.None);
                            if (lag is not null)
                            {
                                replicationLags.Add(new ReplicationLagEntry(replicaId, lag.DelayMs));
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogWarning(ex, "Failed to read replication lag for {ServiceId} while saving run snapshot", replicaId);
                        }
                    }

                    SentinelConfig? sentinel = null;
                    try
                    {
                        sentinel = await docker.GetSentinelConfigAsync(CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to read Sentinel config while saving run snapshot");
                    }

                    // Even with OTEL_BSP_SCHEDULE_DELAY shortened to 1s (see docker-compose.yml),
                    // spans from the tail of the run are still exported on a timer, not the instant
                    // they finish - querying the trace window immediately would systematically miss
                    // recent spans. A short fixed wait is simpler and more honest than pretending to
                    // poll for "enough" spans (there's no way to know the expected count in advance).
                    await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None);

                    var pgcatConnections = await docker.GetPgcatConnectionsAsync(CancellationToken.None);
                    var traceHops = traceStore.GetHopStatsBetween(runStart, runEnd);
                    var verdict = BottleneckAdvisor.Analyze(report, resourceMaxima, traceHops, pgcatConnections);

                    var snapshot = new RunSnapshot(
                        $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid().ToString("N")[..6]}",
                        DateTimeOffset.UtcNow,
                        request,
                        docker.GetInfraStatus(),
                        replicas,
                        pgcatConnections,
                        await docker.GetPostgresConnectionsAsync(CancellationToken.None),
                        report,
                        docker.GetPgcatPoolSettings(),
                        sentinel,
                        replicationLags,
                        docker.GetRabbitMqPrefetch(),
                        docker.GetMongoReadPreference(),
                        docker.GetNpgsqlPoolSize(),
                        resourceMaxima,
                        traceHops,
                        verdict);

                    await runHistory.SaveAsync(snapshot, CancellationToken.None);
                    await hub.Clients.All.SendAsync("runSaved", new { snapshot.Id });
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to save run history snapshot for {Scenario}", request.Scenario);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Traffic run for {Scenario} failed", request.Scenario);
            await hub.Clients.All.SendAsync("trafficFailed", new { request.Scenario, error = ex.Message });
        }
        finally
        {
            Volatile.Write(ref trafficRunActive, 0);
        }
    });

    return Results.Accepted();
});

app.MapGet("/api/endpoints", (IDockerService docker) => Results.Ok(docker.ListKnownEndpoints()));

app.MapGet("/api/data-sources", (IDockerService docker) => Results.Ok(docker.ListDataSources()));

app.MapGet("/api/containers/pgcat/connections", async (IDockerService docker, CancellationToken ct) =>
{
    var result = await docker.GetPgcatConnectionsAsync(ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
});

app.MapGet("/api/containers/postgres/connections", async (IDockerService docker, CancellationToken ct) =>
{
    var result = await docker.GetPostgresConnectionsAsync(ct);
    return result is null ? Results.NotFound() : Results.Ok(result);
});

// One-shot fetch for a page's first paint - TopologyPollerService pushes every change after that
// over the hub ("infraTopologyUpdated"), so the frontend doesn't poll these on its own.
app.MapGet("/api/containers/redis/topology", async (IDockerService docker, CancellationToken ct) =>
    Results.Ok(await docker.GetRedisTopologyAsync(ct)));

app.MapGet("/api/containers/mongo/topology", async (IDockerService docker, CancellationToken ct) =>
    Results.Ok(await docker.GetMongoTopologyAsync(ct)));

app.MapGet("/api/infra/status", (IDockerService docker) => Results.Ok(docker.GetInfraStatus()));

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

app.MapGet("/api/runs", async (IRunHistoryStore store, CancellationToken ct) => Results.Ok(await store.ListAsync(ct)));

app.MapGet("/api/runs/{id}", async (string id, IRunHistoryStore store, CancellationToken ct) =>
{
    var snapshot = await store.GetAsync(id, ct);
    return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
});

app.MapDelete("/api/runs", async (IRunHistoryStore store, CancellationToken ct) =>
{
    await store.ClearAsync(ct);
    return Results.Ok();
});

app.MapDelete("/api/runs/{id}", async (string id, IRunHistoryStore store, CancellationToken ct) =>
    await store.DeleteAsync(id, ct) ? Results.Ok() : Results.NotFound());

app.Run();
