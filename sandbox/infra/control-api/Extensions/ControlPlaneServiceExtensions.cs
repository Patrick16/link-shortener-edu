using ControlApi.Models;
using ControlApi.Services;
using ControlApi.Services.DataSources;
using ControlApi.Services.K6;

namespace ControlApi.Extensions;

public static class ControlPlaneServiceExtensions
{
    // Docker access, the scenario/run-history stores, the in-memory stats/trace stores, SignalR and
    // the pollers that push live state to connected clients.
    public static WebApplicationBuilder AddControlPlaneServices(this WebApplicationBuilder builder)
    {
        var services = builder.Services;

        // Defaults match BottleneckThresholds.Default exactly - an unconfigured deployment (no
        // "BottleneckThresholds" section in appsettings.json) behaves exactly as before this was
        // made configurable.
        services.Configure<BottleneckThresholds>(builder.Configuration.GetSection("BottleneckThresholds"));

        services.AddSingleton<IContainerRuntime, ContainerRuntime>();
        services.AddSingleton<IChaosService, ChaosService>();
        services.AddSingleton<IPgcatService, PgcatService>();
        services.AddSingleton<IHaproxyService, HaproxyService>();
        services.AddSingleton<IPostgresService, PostgresService>();
        services.AddSingleton<IRedisInfraService, RedisInfraService>();
        services.AddSingleton<IMongoTopologyService, MongoTopologyService>();
        services.AddSingleton<IRabbitMqService, RabbitMqService>();
        services.AddSingleton<IInfraToggleService, InfraToggleService>();
        // K6ContainerRunner/DataPoolFetcher - TrafficService's two collaborators (Docker container
        // lifecycle, data-pool preload respectively) - see .notes/PLAN.md's Simplification Backlog
        // item 3.
        services.AddSingleton<K6ContainerRunner>();
        services.AddSingleton<DataPoolFetcher>();
        services.AddSingleton<ITrafficService, TrafficService>();
        // Lesson Mode's traced-request endpoint is the only thing in this app that calls another
        // backend service directly over HTTP (everything else shells out to Docker) - default
        // factory, no named client needed for a single ad-hoc call per request.
        services.AddHttpClient();
        services.AddSingleton<ILessonTraceService, LessonTraceService>();
        services.AddSingleton<IContainerLifecycleService, ContainerLifecycleService>();
        services.AddSingleton<IScenarioStore, ScenarioStore>();
        services.AddSingleton<IPresetStore, PresetStore>();
        services.AddSingleton<IRunHistoryStore, RunHistoryStore>();
        services.AddSingleton<ResourceStatsStore>();
        services.AddSingleton<TraceStore>();
        services.AddSingleton<RunResourceMaxTracker>();
        services.AddSingleton<TrafficRunCoordinator>();
        services.AddSignalR();

        services.AddHostedService<StatusPollerService>();
        services.AddHostedService<ResourceStatsPollerService>();
        services.AddHostedService<TopologyPollerService>();
        services.AddHostedService<SentinelSelfHealPollerService>();
        services.AddHostedService<ToggleStateReconcilerService>();
        return builder;
    }

    public static WebApplicationBuilder AddFrontendCors(this WebApplicationBuilder builder)
    {
        var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
            ?? ["http://localhost:5173", "http://localhost:5174"];
        builder.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(policy =>
                policy.WithOrigins(origins).AllowAnyMethod().AllowAnyHeader().AllowCredentials());
        });
        return builder;
    }
}
