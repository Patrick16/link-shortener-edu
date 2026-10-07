using ControlApi.Models;

namespace ControlApi.Services;

public interface IDockerService
{
    Task<IReadOnlyList<ManagedContainer>> ListContainersAsync(CancellationToken ct);

    Task<ManagedContainer?> StopAsync(string serviceId, CancellationToken ct);

    Task<ManagedContainer?> StartAsync(string serviceId, CancellationToken ct);

    Task<ManagedContainer?> RestartAsync(string serviceId, CancellationToken ct);

    // Runs the request's ordered Endpoints sequence through k6-scripts/flow.js against the real
    // stack, calling onProgress roughly once a second (parsed from k6's own periodic status lines)
    // while it runs, then returns the final report. Null return means an endpoint id didn't
    // resolve against ListKnownEndpoints (already rejected by Program.cs before this is called).
    Task<TrafficReport?> RunTrafficAsync(TrafficRequest request, Func<TrafficProgress, Task> onProgress, CancellationToken ct);

    // Current CPU/memory/TCP snapshot for one specific container - takes the ManagedContainer
    // itself (already has its ContainerId) rather than a serviceId, so a caller sampling every
    // replica of a scaled service gets each container's own reading instead of every call
    // re-resolving "the" container for that service and landing on the same one each time.
    Task<ResourceSample?> GetResourceSampleAsync(ManagedContainer container, CancellationToken ct);

    // Names of services this instance will scale (an explicit allowlist, not "anything in
    // the compose file" - only ones nginx actually fronts have a reason to run >1 replica).
    IReadOnlyList<string> ListScalableServices();

    // Shells out to `docker compose ... up -d --scale <serviceId>=<replicas>` - the actual
    // replica-management logic is Compose's own, not reimplemented against the raw Docker API.
    Task<ScaleResult> ScaleAsync(string serviceId, int replicas, CancellationToken ct);

    // The four standing infra toggles - see InfraStatus for what each one actually does and why
    // they're not all implemented the same way (one's an in-memory flag, three recreate containers).
    InfraStatus GetInfraStatus();
    InfraStatus SetNginxBypass(bool bypassed);
    Task<InfraStatus> SetPgcatEnabledAsync(bool enabled, CancellationToken ct);
    Task<InfraStatus> SetCacheEnabledAsync(bool enabled, CancellationToken ct);
    Task<InfraStatus> SetMessagingModeAsync(string mode, CancellationToken ct);

    // The real routes a traffic run's step sequence can be built from - see EndpointDefinition and
    // k6-scripts/flow.js.
    IReadOnlyList<EndpointDefinition> ListKnownEndpoints();

    // The bulk, paginated reads a run's DataPoolRequest can preload from - see DataSourceDefinition.
    IReadOnlyList<DataSourceDefinition> ListDataSources();

    // Consumer QoS - read once at RabbitMqConsumer startup, so this recreates shortener-service and
    // traffic-service (same env-var + --force-recreate --no-deps shape as the pgcat/cache toggles).
    int GetRabbitMqPrefetch();
    Task<int> SetRabbitMqPrefetchAsync(int prefetchCount, CancellationToken ct);

    // readPreference on traffic-service's Mongo connection string - "primary" or
    // "secondaryPreferred". Recreates just traffic-service.
    string GetMongoReadPreference();
    Task<string> SetMongoReadPreferenceAsync(string preference, CancellationToken ct);

    // Npgsql's own client-side "Maximum Pool Size" on every DB-touching service's connection string -
    // the client-pool-size half of the picture pgcat's own connection-stats panel already shows the
    // server-pool-size half of. Recreates the same DbTouchingServices set the pgcat toggle does.
    int GetNpgsqlPoolSize();
    Task<int> SetNpgsqlPoolSizeAsync(int poolSize, CancellationToken ct);
}
