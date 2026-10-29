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

    // L1 (in-process) layer ahead of Redis - deliberately NOT part of InfraStatus (see its own
    // comment on why) - same "own GET/POST pair, not staged in Topology" shape as
    // GetRabbitMqPrefetch/GetMongoReadPreference/GetNpgsqlPoolSize below. Recreates the same
    // CacheUsingServices (link-api/redirect-api) SetCacheEnabledAsync does, with a different env
    // var - the two toggles are independent, so Redis can be compared "with" vs. "without" L1
    // while staying on throughout.
    bool GetL1CacheEnabled();

    Task<bool> SetL1CacheEnabledAsync(bool enabled, CancellationToken ct);

    // Negative caching (a short-TTL "this id doesn't exist" marker, see EntityCacheService) -
    // same "own GET/POST pair, not InfraStatus/Topology" shape as L1 above, same recreate scope
    // (CacheUsingServices). Independent of both CacheEnabled and L1CacheEnabled.
    bool GetNegativeCacheEnabled();

    Task<bool> SetNegativeCacheEnabledAsync(bool enabled, CancellationToken ct);

    Task<InfraStatus> SetMessagingModeAsync(string mode, CancellationToken ct);

    // Starts/stops the haproxy container itself, not a connection-string flip on its consumers -
    // see InfraStatus's own comment on HaproxyEnabled for why.
    Task<InfraStatus> SetHaproxyEnabledAsync(bool enabled, CancellationToken ct);

    // Stops/starts postgres-replica1/2 themselves (volumes kept, so a later ON resumes streaming
    // from the existing replication slot instead of a fresh pg_basebackup) and keeps pgcat.toml's
    // servers list in sync - see InfraStatus's own comment on PostgresReplicasEnabled.
    Task<InfraStatus> SetPostgresReplicasEnabledAsync(bool enabled, CancellationToken ct);

    // Starts/stops redis-master itself AND moves CacheEnabled with it, both directions - a running
    // redis-master nobody's allowed to read from has no reason to exist on its own. Also cascades
    // Sentinel off on OFF (nothing left to monitor/fail over) - see InfraStatus's own comment on
    // RedisEnabled/RedisSentinelEnabled.
    Task<InfraStatus> SetRedisEnabledAsync(bool enabled, CancellationToken ct);

    // Starts/stops redis-sentinel-1/2/3 and redis-replica1/2 (volumes kept, same reasoning as
    // postgres-replica1/2 - their entrypoints already self-heal role/config on restart) and recreates
    // the 6 Redis-using services with the matching connection-string form (Sentinel-aware vs direct
    // redis-master:6379). Only legal to turn on while Redis itself is on.
    Task<InfraStatus> SetRedisSentinelEnabledAsync(bool enabled, CancellationToken ct);

    // Drops/re-adds mongo2/mongo3 from rs0's own voting configuration (via
    // IMongoTopologyService.SetReplicasEnabledAsync, not just a container stop/start - see its own
    // comment for why a stop alone would be dangerous here) and recreates traffic-service with the
    // matching ConnectionStrings__Mongo host list - see InfraStatus's own comment on
    // MongoReplicasEnabled.
    Task<InfraStatus> SetMongoReplicasEnabledAsync(bool enabled, CancellationToken ct);

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

    // Bulkhead isolation (resilience backlog item 15) - the concurrency limiter's permit limit on
    // traffic-service/reporting-service's shared ip-api.com HttpClient (see
    // GeoIpExtensions.AddGeoIpResolution). Read once at startup, same "own GET/POST pair, recreate
    // with a different env var" shape as RabbitMqPrefetch/NpgsqlPoolSize above - no node in
    // architecture.json represents ip-api.com itself, so this one attaches directly to the two
    // consumer-service nodes instead of a shared dependency node.
    int GetGeoIpBulkheadLimit();

    Task<int> SetGeoIpBulkheadLimitAsync(int limit, CancellationToken ct);

    // Hedged reads (resilience backlog follow-up) - the delay HedgedClickMetaQueryService waits
    // before firing its second, redundant query against clicks_meta_db's other replica (see
    // MongoExtensions.AddMongoClickMetaReader). Same "own GET/POST pair, recreate with a different
    // env var" shape as GeoIpBulkheadLimit above, attached to reporting-api alone - it's the only
    // service that ever queries clicks_meta_db for reads.
    int GetMongoHedgeDelayMs();

    Task<int> SetMongoHedgeDelayMsAsync(int delayMs, CancellationToken ct);

    // Resilience backlog item 1's own on/off switch (Polly circuit breakers for Redis, gRPC,
    // RabbitMQ and ip-api.com - see ResiliencePipelines/GeoIpExtensions). Same "own GET/POST pair,
    // recreate with a different env var" shape as L1CacheEnabled above, not InfraStatus/Topology -
    // it doesn't change which nodes are in the active request path either. Unlike every other
    // toggle in this file, it touches all 6 breaker-registering services at once (auth-api/
    // link-api/redirect-api/shortener-service/traffic-service/reporting-service - see
    // CircuitBreakerUsingServices), not just one DB- or cache-touching pair.
    bool GetCircuitBreakersEnabled();

    Task<bool> SetCircuitBreakersEnabledAsync(bool enabled, CancellationToken ct);
}
