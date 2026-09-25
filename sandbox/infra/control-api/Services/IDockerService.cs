using ControlApi.Models;

namespace ControlApi.Services;

public interface IDockerService
{
    Task<IReadOnlyList<ManagedContainer>> ListContainersAsync(CancellationToken ct);

    Task<ManagedContainer?> StopAsync(string serviceId, CancellationToken ct);

    Task<ManagedContainer?> StartAsync(string serviceId, CancellationToken ct);

    Task<ManagedContainer?> RestartAsync(string serviceId, CancellationToken ct);

    // Runs Pumba against the target's network - the target container itself keeps running
    // throughout, only its network behaves worse. Self-heals after DurationSeconds; returns null
    // if the target service isn't found.
    Task<ChaosAction?> DegradeAsync(string serviceId, ChaosRequest request, CancellationToken ct);

    // Stops any in-progress chaos against a service early instead of waiting out its duration.
    // Returns how many chaos containers were stopped.
    Task<int> HealAsync(string serviceId, CancellationToken ct);

    // Runs the request's ordered Endpoints sequence through k6-scripts/flow.js against the real
    // stack, calling onProgress roughly once a second (parsed from k6's own periodic status lines)
    // while it runs, then returns the final report. Null return means an endpoint id didn't
    // resolve against ListKnownEndpoints (already rejected by Program.cs before this is called).
    Task<TrafficReport?> RunTrafficAsync(TrafficRequest request, Func<TrafficProgress, Task> onProgress, CancellationToken ct);

    // Current CPU/memory snapshot for one service, or null if it isn't running.
    Task<ResourceSample?> GetResourceSampleAsync(string serviceId, CancellationToken ct);

    // Names of services this instance will scale (an explicit allowlist, not "anything in
    // the compose file" - only ones nginx actually fronts have a reason to run >1 replica).
    IReadOnlyList<string> ListScalableServices();

    // Shells out to `docker compose ... up -d --scale <serviceId>=<replicas>` - the actual
    // replica-management logic is Compose's own, not reimplemented against the raw Docker API.
    Task<ScaleResult> ScaleAsync(string serviceId, int replicas, CancellationToken ct);

    // Runs `redis-cli FLUSHALL` inside the redis container via Docker's exec API - lets a demo
    // show cold-cache behavior on demand. Returns the command's own output, or null if redis isn't
    // running.
    Task<string?> FlushRedisAsync(CancellationToken ct);

    // The three standing infra toggles - see InfraStatus for what each one actually does and why
    // they're not all implemented the same way (one's an in-memory flag, two recreate containers).
    InfraStatus GetInfraStatus();
    InfraStatus SetNginxBypass(bool bypassed);
    Task<InfraStatus> SetPgcatEnabledAsync(bool enabled, CancellationToken ct);
    Task<InfraStatus> SetCacheEnabledAsync(bool enabled, CancellationToken ct);

    // The real routes a traffic run's step sequence can be built from - see EndpointDefinition and
    // k6-scripts/flow.js.
    IReadOnlyList<EndpointDefinition> ListKnownEndpoints();

    // The bulk, paginated reads a run's DataPoolRequest can preload from - see DataSourceDefinition.
    IReadOnlyList<DataSourceDefinition> ListDataSources();

    // Live connection counts, read directly off pgcat/postgres via `psql` in a Docker exec (same
    // approach as FlushRedisAsync) - not polled/cached, a fresh snapshot on every call. Null means
    // the container isn't running.
    Task<PgcatConnectionStats?> GetPgcatConnectionsAsync(CancellationToken ct);
    Task<PostgresConnectionStats?> GetPostgresConnectionsAsync(CancellationToken ct);

    // Which physical container is actually master/primary right now, read directly off each
    // cluster's own data-plane nodes rather than assumed from architecture.json's static labels -
    // both Redis Sentinel and MongoDB's replica set can re-elect a leader with zero involvement from
    // this app, and the graph needs to reflect that instead of silently going stale. See NodeRole.
    Task<InfraTopology> GetRedisTopologyAsync(CancellationToken ct);
    Task<InfraTopology> GetMongoTopologyAsync(CancellationToken ct);

    // pgcat.toml pool_mode/read-write-splitting/pool_size - rewrites the file directly and relies on
    // pgcat's own autoreload, no docker compose recreate involved. See PgcatPoolSettings.
    PgcatPoolSettings GetPgcatPoolSettings();
    Task<PgcatPoolSettings> SetPgcatPoolSettingsAsync(PgcatPoolSettings settings, CancellationToken ct);

    // Artificial WAL-replay delay on one Postgres standby (postgres-replica1/postgres-replica2) via
    // recovery_min_apply_delay - live SQL against the target container, no file edit or restart.
    // Null means the given serviceId isn't a known replica.
    Task<ReplicationLag?> GetReplicationLagAsync(string serviceId, CancellationToken ct);
    Task<ReplicationLag?> SetReplicationLagAsync(string serviceId, int delayMs, CancellationToken ct);

    // Redis Sentinel's own live SENTINEL SET/MASTER commands - applied to all 3 sentinel containers
    // at once (each tracks its own local config independently). See SentinelConfig.
    Task<SentinelConfig?> GetSentinelConfigAsync(CancellationToken ct);
    Task<SentinelConfig> SetSentinelConfigAsync(SentinelConfig config, CancellationToken ct);

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
