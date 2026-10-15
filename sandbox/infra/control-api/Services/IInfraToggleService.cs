using ControlApi.Models;

namespace ControlApi.Services;

public interface IInfraToggleService
{
    // The standing infra toggles - see InfraStatus for what each one actually does and why
    // they're not all implemented the same way (one's an in-memory flag, most recreate containers,
    // and HaproxyEnabled is read live rather than cached at all). Async because assembling it reads
    // haproxy's actual Docker state on every call - see InfraStatus's own comment on HaproxyEnabled.
    Task<InfraStatus> GetInfraStatusAsync(CancellationToken ct);

    Task<InfraStatus> SetNginxBypassAsync(bool bypassed, CancellationToken ct);

    Task<InfraStatus> SetPgcatEnabledAsync(bool enabled, CancellationToken ct);

    Task<InfraStatus> SetCacheEnabledAsync(bool enabled, CancellationToken ct);

    Task<InfraStatus> SetMessagingModeAsync(string mode, CancellationToken ct);

    // Starts/stops the haproxy container itself, not a connection-string flip on its consumers -
    // see InfraStatus's own comment on HaproxyEnabled for why.
    Task<InfraStatus> SetHaproxyEnabledAsync(bool enabled, CancellationToken ct);

    // Stops/starts postgres-replica1/2 themselves (volumes kept, so a later ON resumes streaming
    // from the existing replication slot instead of a fresh pg_basebackup) and keeps pgcat.toml's
    // servers list in sync - see InfraStatus's own comment on PostgresReplicasEnabled.
    Task<InfraStatus> SetPostgresReplicasEnabledAsync(bool enabled, CancellationToken ct);

    // Consumer QoS - read once at RabbitMqConsumer startup, so this recreates shortener-service and
    // traffic-service (same env-var + --force-recreate --no-deps shape as the pgcat/cache toggles).
    int GetRabbitMqPrefetch();

    Task<int> SetRabbitMqPrefetchAsync(int prefetchCount, CancellationToken ct);

    // readPreference on traffic-service's Mongo connection string - "primary" or
    // "secondaryPreferred". Recreates just traffic-service.
    string GetMongoReadPreference();

    Task<string> SetMongoReadPreferenceAsync(string preference, CancellationToken ct);

    // Npgsql's own client-side "Maximum Pool Size" on every DB-touching service's connection
    // string - the client-pool-size half of the picture pgcat's own connection-stats panel already
    // shows the server-pool-size half of. Recreates the same set of services the pgcat toggle does.
    int GetNpgsqlPoolSize();

    Task<int> SetNpgsqlPoolSizeAsync(int poolSize, CancellationToken ct);
}
