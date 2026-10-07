using ControlApi.Models;
using ControlApi.Services;

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
        services.AddSingleton<IPostgresService, PostgresService>();
        services.AddSingleton<IRedisInfraService, RedisInfraService>();
        services.AddSingleton<IMongoTopologyService, MongoTopologyService>();
        services.AddSingleton<IRabbitMqService, RabbitMqService>();
        services.AddSingleton<IInfraToggleService, InfraToggleService>();
        services.AddSingleton<ITrafficService, TrafficService>();
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
