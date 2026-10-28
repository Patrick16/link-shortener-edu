using ControlApi.Models;
using Docker.DotNet.Models;

namespace ControlApi.Services;

// The 7 standing infra toggles, kept together deliberately - not a reluctant leftover from the
// god-class split, but a real coupled unit. Every handler below recreates a container shared with
// at least one other toggle (DbTouchingServices/CacheUsingServices overlap on link-api/
// redirect-api; DbTouchingServices/RabbitMqConsumingServices/traffic-service overlap on
// shortener-service/traffic-service), and `docker compose up` only applies the env vars THIS ONE
// invocation passes - every other var falls back to its docker-compose.yml default. Recreating a
// shared service for only one axis would silently revert every other axis on that container to its
// default, even though nothing else changed and the UI still shows the old value. Splitting these
// into separate classes with no shared state would reintroduce exactly that bug; CurrentStandingEnv()
// is what every handler merges before recreating, so triggering any one toggle re-applies every
// other toggle's current value too instead of quietly undoing it.
public class InfraToggleService(IContainerRuntime runtime, IPgcatService pgcat, IRedisInfraService redis, IMongoTopologyService mongo, ILogger<InfraToggleService> logger) : IInfraToggleService
{
    // The two streaming replicas this toggle stops/starts - see SetPostgresReplicasEnabledAsync.
    private static readonly IReadOnlyList<string> PostgresReplicaContainers = ["postgres-replica1", "postgres-replica2"];

    // The two rs0 members this toggle stops/starts (mongo1 is never touched - see
    // SetMongoReplicasEnabledAsync) and the two ConnectionStrings__Mongo host-list forms
    // SetMongoReplicasEnabledAsync switches traffic-service between - mirrors docker-compose.yml's
    // own shipped default (all 3) exactly.
    private static readonly IReadOnlyList<string> MongoReplicaContainers = ["mongo2", "mongo3"];
    private const string MongoAllHosts = "mongo1:27017,mongo2:27017,mongo3:27017";
    private const string MongoPrimaryOnlyHost = "mongo1:27017";

    // The 5 containers that make up Redis's HA layer on top of the single redis-master - this
    // toggle's own stops/starts, see SetRedisSentinelEnabledAsync.
    private static readonly IReadOnlyList<string> RedisHaContainers = ["redis-sentinel-1", "redis-sentinel-2", "redis-sentinel-3", "redis-replica1", "redis-replica2"];

    // Every service that reads ConnectionStrings__Redis (see docker-compose.yml's REDIS_CONN
    // indirection) - the Sentinel toggle recreates all 6 together so none of them is left pointed at
    // a connection-string form the other 5 have already moved on from.
    private static readonly IReadOnlyList<string> RedisUsingServices =
        ["link-api", "redirect-api", "shortener-service", "traffic-service", "reporting-service", "reporting-api"];

    // The two REDIS_CONN forms SetRedisSentinelEnabledAsync switches the 6 services above between -
    // mirrors docker-compose.yml's own default (Sentinel-form) exactly, so this toggle's ON state
    // reproduces the shipped stack rather than some other equivalent string.
    private const string RedisSentinelConnectionString = "redis-sentinel-1:26379,redis-sentinel-2:26379,redis-sentinel-3:26379,serviceName=mymaster";
    private const string RedisDirectConnectionString = "redis-master:6379";

    // Every DB-touching service - pgcat's toggle recreates all of them, since "the system without
    // connection pooling" means the whole system, not just the two APIs a k6 test happens to hit.
    private static readonly IReadOnlyList<string> DbTouchingServices =
        ["auth-api", "link-api", "redirect-api", "shortener-service", "traffic-service"];

    // Only these two actually read/populate the Redis cache (see LinkCacheService) - shortener-
    // service/traffic-service/auth-api have no cache to disable.
    private static readonly IReadOnlyList<string> CacheUsingServices = ["link-api", "redirect-api"];

    // Only these two actually branch on Messaging:Mode (IEventDispatcher's DI registration) -
    // shortener-service/traffic-service/reporting-service always host their gRPC endpoint
    // regardless of mode (see GrpcMessagingExtensions.AddMessagingGrpcServer's own comment), so
    // they never need recreating when this toggle flips, unlike every other DB-touching toggle.
    private static readonly IReadOnlyList<string> MessagingUsingServices = ["link-api", "redirect-api"];

    // The three RabbitMQ consumers - workers that each run a LinkCreatedConsumer and/or
    // ClickTrackedConsumer, all sharing RabbitMqConsumer's own _prefetchCount field.
    // reporting-service was missing here for a while after it was added as a third click.tracked
    // consumer (CQRS/ClickHouse feature) - its docker-compose.yml block never got the
    // RabbitMq__PrefetchCount/BatchSize/BatchTimeoutMs env vars either, so this toggle silently
    // never touched it: no matter what prefetch was set here, reporting-service stayed on
    // RabbitMqConsumer's hardcoded defaults (prefetch=10), capping its throughput regardless of
    // this control or how many replicas were running (found live - reported as "only ~70 msg/s
    // no matter the QoS/replica count" while shortener-service/traffic-service scaled fine).
    private static readonly IReadOnlyList<string> RabbitMqConsumingServices =
        ["shortener-service", "traffic-service", "reporting-service"];

    // The two services that call GeoIpExtensions.AddGeoIpResolution - see SetGeoIpBulkheadLimitAsync.
    private static readonly IReadOnlyList<string> GeoIpUsingServices = ["traffic-service", "reporting-service"];

    // In-memory standing toggle state - see InfraStatus for why this is fine for a local sandbox
    // tool despite not surviving a control-api restart.
    private bool _nginxBypassed;
    private bool _pgcatEnabled = true;
    private bool _cacheEnabled = true;
    // Mirrors EntityCacheService's own default (Cache:L1Enabled, no key set => enabled).
    private bool _l1CacheEnabled = true;
    // Mirrors EntityCacheService's own default (Cache:NegativeEnabled, no key set => enabled).
    private bool _negativeCacheEnabled = true;
    private string _messagingMode = "rabbitmq";

    // Mirrors RabbitMqConsumer's own hardcoded-turned-configurable default.
    private int _rabbitMqPrefetch = 10;

    // Mirrors the shipped default in docker-compose.yml's ConnectionStrings__Mongo.
    private string _mongoReadPreference = "primary";

    // Mirrors Npgsql's own default (and the shipped ${NPGSQL_MAX_POOL_SIZE:-100} fallback).
    private int _npgsqlPoolSize = 100;

    // Mirrors GeoIpExtensions.DefaultMaxConcurrentRequests (and the shipped
    // ${GEOIP_BULKHEAD_LIMIT:-10} fallback).
    private int _geoIpBulkheadLimit = 10;

    // Drives REDIS_CONN below - same cached, recreate-with-a-different-env-var shape as
    // _pgcatEnabled/_cacheEnabled/_messagingMode, not the live-read shape RedisEnabled/
    // RedisSentinelEnabled themselves use. See InfraStatus's own comment on RedisEnabled/
    // RedisSentinelEnabled for the full reasoning.
    private bool _redisConnectionViaSentinel = true;

    // Drives MONGO_HOSTS below - same cached, recreate-with-a-different-env-var shape as
    // _redisConnectionViaSentinel above, not the live-read shape MongoReplicasEnabled itself uses.
    // See InfraStatus's own comment on MongoReplicasEnabled for the full reasoning.
    private bool _mongoConnectionIncludesReplicas = true;

    // Every axis below recreates a service `docker compose up --force-recreate` can also touch via
    // a DIFFERENT toggle - see this class's own header comment. Every handler merges this full
    // current state and overrides only its own key before recreating, so triggering any one toggle
    // re-applies every other toggle's current value too instead of quietly undoing it.
    internal Dictionary<string, string> CurrentStandingEnv() => new()
    {
        // "haproxy" (not "pgcat") since the Pooler Scaling feature (2026-10-09) - haproxy is the
        // single entry point in front of the 3 pgcat instances now, see docker-compose.yml.
        ["DB_HOST"] = _pgcatEnabled ? "haproxy" : "postgres",
        ["DB_PORT"] = _pgcatEnabled ? "6432" : "5432",
        ["CACHE_ENABLED"] = _cacheEnabled ? "true" : "false",
        ["L1_CACHE_ENABLED"] = _l1CacheEnabled ? "true" : "false",
        ["NEGATIVE_CACHE_ENABLED"] = _negativeCacheEnabled ? "true" : "false",
        ["MESSAGING_MODE"] = _messagingMode,
        ["NPGSQL_MAX_POOL_SIZE"] = _npgsqlPoolSize.ToString(),
        ["GEOIP_BULKHEAD_LIMIT"] = _geoIpBulkheadLimit.ToString(),
        ["RABBITMQ_PREFETCH"] = _rabbitMqPrefetch.ToString(),
        ["MONGO_READ_PREFERENCE"] = _mongoReadPreference,
        ["REDIS_CONN"] = _redisConnectionViaSentinel ? RedisSentinelConnectionString : RedisDirectConnectionString,
        ["MONGO_HOSTS"] = _mongoConnectionIncludesReplicas ? MongoAllHosts : MongoPrimaryOnlyHost,
    };

    public int GetRabbitMqPrefetch() => _rabbitMqPrefetch;

    public Task<int> SetRabbitMqPrefetchAsync(int prefetchCount, CancellationToken ct) =>
        runtime.RunExclusiveToggleAsync(async ct =>
        {
            var env = CurrentStandingEnv();
            env["RABBITMQ_PREFETCH"] = prefetchCount.ToString();

            var scaleArgs = await runtime.BuildPreserveScaleArgsAsync(RabbitMqConsumingServices, ct);

            logger.LogWarning("Switching RabbitMq__PrefetchCount for {Services} to {PrefetchCount}", string.Join(", ", RabbitMqConsumingServices), prefetchCount);
            var (exitCode, output) = await runtime.RunComposeAsync(["up", "-d", "--force-recreate", "--no-deps", .. scaleArgs, .. RabbitMqConsumingServices], env, ct);
            if (exitCode != 0)
            {
                logger.LogWarning("Setting RabbitMQ prefetch failed (exit {ExitCode}): {Output}", exitCode, output);
                throw new InvalidOperationException($"docker compose exited {exitCode}: {output}");
            }

            _rabbitMqPrefetch = prefetchCount;
            return prefetchCount;
        }, ct);

    public string GetMongoReadPreference() => _mongoReadPreference;

    public Task<string> SetMongoReadPreferenceAsync(string preference, CancellationToken ct) =>
        runtime.RunExclusiveToggleAsync(async ct =>
        {
            var env = CurrentStandingEnv();
            env["MONGO_READ_PREFERENCE"] = preference;

            logger.LogWarning("Switching traffic-service's Mongo readPreference to {Preference}", preference);
            var (exitCode, output) = await runtime.RunComposeAsync(["up", "-d", "--force-recreate", "--no-deps", "traffic-service"], env, ct);
            if (exitCode != 0)
            {
                logger.LogWarning("Setting Mongo read preference failed (exit {ExitCode}): {Output}", exitCode, output);
                throw new InvalidOperationException($"docker compose exited {exitCode}: {output}");
            }

            _mongoReadPreference = preference;
            return preference;
        }, ct);

    public int GetNpgsqlPoolSize() => _npgsqlPoolSize;

    public Task<int> SetNpgsqlPoolSizeAsync(int poolSize, CancellationToken ct) =>
        runtime.RunExclusiveToggleAsync(async ct =>
        {
            var env = CurrentStandingEnv();
            env["NPGSQL_MAX_POOL_SIZE"] = poolSize.ToString();
            var scaleArgs = await runtime.BuildPreserveScaleArgsAsync(DbTouchingServices, ct);

            logger.LogWarning("Switching Npgsql Maximum Pool Size for {Services} to {PoolSize}", string.Join(", ", DbTouchingServices), poolSize);
            var (exitCode, output) = await runtime.RunComposeAsync(["up", "-d", "--force-recreate", "--no-deps", .. scaleArgs, .. DbTouchingServices], env, ct);
            if (exitCode != 0)
            {
                logger.LogWarning("Setting Npgsql pool size failed (exit {ExitCode}): {Output}", exitCode, output);
                throw new InvalidOperationException($"docker compose exited {exitCode}: {output}");
            }

            _npgsqlPoolSize = poolSize;
            return poolSize;
        }, ct);

    public int GetGeoIpBulkheadLimit() => _geoIpBulkheadLimit;

    public Task<int> SetGeoIpBulkheadLimitAsync(int limit, CancellationToken ct) =>
        runtime.RunExclusiveToggleAsync(async ct =>
        {
            var env = CurrentStandingEnv();
            env["GEOIP_BULKHEAD_LIMIT"] = limit.ToString();
            var scaleArgs = await runtime.BuildPreserveScaleArgsAsync(GeoIpUsingServices, ct);

            logger.LogWarning("Switching GeoIp__MaxConcurrentRequests for {Services} to {Limit}", string.Join(", ", GeoIpUsingServices), limit);
            var (exitCode, output) = await runtime.RunComposeAsync(["up", "-d", "--force-recreate", "--no-deps", .. scaleArgs, .. GeoIpUsingServices], env, ct);
            if (exitCode != 0)
            {
                logger.LogWarning("Setting geo-IP bulkhead limit failed (exit {ExitCode}): {Output}", exitCode, output);
                throw new InvalidOperationException($"docker compose exited {exitCode}: {output}");
            }

            _geoIpBulkheadLimit = limit;
            return limit;
        }, ct);

    // See InfraStatus's own comment on HaproxyEnabled/PostgresReplicasEnabled/RedisEnabled/
    // RedisSentinelEnabled/MongoReplicasEnabled for why those five fields are read live here instead
    // of joining _nginxBypassed/_pgcatEnabled/_cacheEnabled/_messagingMode as cached state.
    public async Task<InfraStatus> GetInfraStatusAsync(CancellationToken ct) =>
        new(
            _nginxBypassed,
            _pgcatEnabled,
            _cacheEnabled,
            _messagingMode,
            await IsHaproxyRunningAsync(ct),
            await AreAllRunningAsync(PostgresReplicaContainers, ct),
            await IsRedisMasterRunningAsync(ct),
            await AreAllRunningAsync(RedisHaContainers, ct),
            await AreAllRunningAsync(MongoReplicaContainers, ct));

    private async Task<bool> IsHaproxyRunningAsync(CancellationToken ct) =>
        await runtime.FindAsync("haproxy", ct) is { State: "running" };

    private async Task<bool> IsRedisMasterRunningAsync(CancellationToken ct) =>
        await runtime.FindAsync("redis-master", ct) is { State: "running" };

    private async Task<bool> AreAllRunningAsync(IReadOnlyList<string> serviceIds, CancellationToken ct)
    {
        foreach (var name in serviceIds)
        {
            if (await runtime.FindAsync(name, ct) is not { State: "running" })
            {
                return false;
            }
        }

        return true;
    }

    public async Task<InfraStatus> SetNginxBypassAsync(bool bypassed, CancellationToken ct)
    {
        logger.LogWarning("Nginx bypass for load-test traffic: {Bypassed}", bypassed);
        _nginxBypassed = bypassed;
        return await GetInfraStatusAsync(ct);
    }

    // --no-deps is load-bearing, not cosmetic: --force-recreate on named services also recreates
    // their whole depends_on chain unless told not to (found by hitting it - a pgcat toggle here
    // once cascaded into recreating postgres/redis/rabbitmq/pgcat itself too). That's doubly bad
    // for any service with a *relative* bind mount (pgcat's own pgcat.toml): this docker compose
    // process runs INSIDE control-api, so a path like "./infra/pgcat/pgcat.toml" resolves against
    // control-api's own /workspace view, but the actual container is created by the host's Docker
    // daemon over the socket, which resolves that same string against the real host filesystem -
    // where it doesn't exist, so Docker silently creates it as an empty directory and the mount
    // fails. --no-deps keeps this to exactly the named services, none of which have any volumes.
    public Task<InfraStatus> SetPgcatEnabledAsync(bool enabled, CancellationToken ct) =>
        runtime.RunExclusiveToggleAsync(async ct =>
        {
            var env = CurrentStandingEnv();
            env["DB_HOST"] = enabled ? "haproxy" : "postgres";
            env["DB_PORT"] = enabled ? "6432" : "5432";
            var scaleArgs = await runtime.BuildPreserveScaleArgsAsync(DbTouchingServices, ct);

            logger.LogWarning("Switching DB routing for {Services} to {Host} (pgcat enabled: {Enabled})", string.Join(", ", DbTouchingServices), env["DB_HOST"], enabled);
            var (exitCode, output) = await runtime.RunComposeAsync(["up", "-d", "--force-recreate", "--no-deps", .. scaleArgs, .. DbTouchingServices], env, ct);
            if (exitCode != 0)
            {
                logger.LogWarning("Toggling pgcat failed (exit {ExitCode}): {Output}", exitCode, output);
                throw new InvalidOperationException($"docker compose exited {exitCode}: {output}");
            }

            _pgcatEnabled = enabled;

            // One-directional, deliberately: disabling pgcat means the whole pooler layer is off, so
            // it also stops haproxy rather than leaving it dangling in front of nothing. Re-enabling
            // pgcat does NOT symmetrically start haproxy back up, though - bringing a node up is an
            // explicit action (the sub-toggle + Save/Apply), never an automatic side effect of
            // flipping something else, so haproxy stays off until the user turns it back on
            // themselves. Calls the un-gated helpers directly, not SetHaproxyEnabledAsync itself -
            // this whole delegate already runs inside RunExclusiveToggleAsync's gate, which isn't
            // reentrant. Also collapses pgcat to 1 replica, same as SetHaproxyEnabledAsync(false)
            // does standalone - found live: calling this endpoint directly (bypassing the Topology
            // panel's UI, which always re-applies both toggles together and happens to self-heal
            // this) could otherwise leave pgcat at 3 idle replicas with haproxy off, contradicting
            // SyncPgcatReplicaCountAsync's own "always exactly 1 or 3, in lockstep with haproxy"
            // invariant.
            if (!enabled)
            {
                await SyncHaproxyContainerAsync(false, ct);
                await SyncPgcatReplicaCountAsync(1, ct);
            }

            return await GetInfraStatusAsync(ct);
        }, ct);

    public Task<InfraStatus> SetCacheEnabledAsync(bool enabled, CancellationToken ct) =>
        runtime.RunExclusiveToggleAsync(async ct =>
        {
            _cacheEnabled = enabled;
            await RecreateCacheUsingServicesAsync(ct);
            return await GetInfraStatusAsync(ct);
        }, ct);

    public bool GetL1CacheEnabled() => _l1CacheEnabled;

    public Task<bool> SetL1CacheEnabledAsync(bool enabled, CancellationToken ct) =>
        runtime.RunExclusiveToggleAsync(async ct =>
        {
            _l1CacheEnabled = enabled;
            await RecreateCacheUsingServicesAsync(ct);
            return _l1CacheEnabled;
        }, ct);

    public bool GetNegativeCacheEnabled() => _negativeCacheEnabled;

    public Task<bool> SetNegativeCacheEnabledAsync(bool enabled, CancellationToken ct) =>
        runtime.RunExclusiveToggleAsync(async ct =>
        {
            _negativeCacheEnabled = enabled;
            await RecreateCacheUsingServicesAsync(ct);
            return _negativeCacheEnabled;
        }, ct);

    // Shared by SetCacheEnabledAsync/SetL1CacheEnabledAsync/SetNegativeCacheEnabledAsync - all
    // three recreate the exact same two services (CacheUsingServices), just with a different one
    // of CurrentStandingEnv's cache-related env vars changed first. Also shared by
    // SetRedisEnabledAsync's ON path (which sets _cacheEnabled directly and calls this instead of
    // the public, gated method, same reasoning as every other un-gated private helper in this
    // class) - always reads the fields rather than taking a parameter, the caller sets them first,
    // this just recreates to match.
    private async Task RecreateCacheUsingServicesAsync(CancellationToken ct)
    {
        var env = CurrentStandingEnv();
        var scaleArgs = await runtime.BuildPreserveScaleArgsAsync(CacheUsingServices, ct);

        logger.LogWarning(
            "Switching Cache__Enabled/Cache__L1Enabled/Cache__NegativeEnabled for {Services} to {Enabled}/{L1Enabled}/{NegativeEnabled}",
            string.Join(", ", CacheUsingServices), _cacheEnabled, _l1CacheEnabled, _negativeCacheEnabled);
        var (exitCode, output) = await runtime.RunComposeAsync(["up", "-d", "--force-recreate", "--no-deps", .. scaleArgs, .. CacheUsingServices], env, ct);
        if (exitCode != 0)
        {
            logger.LogWarning("Toggling cache failed (exit {ExitCode}): {Output}", exitCode, output);
            throw new InvalidOperationException($"docker compose exited {exitCode}: {output}");
        }
    }

    public Task<InfraStatus> SetMessagingModeAsync(string mode, CancellationToken ct)
    {
        if (mode is not ("rabbitmq" or "grpc"))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Messaging mode must be 'rabbitmq' or 'grpc'.");
        }

        return runtime.RunExclusiveToggleAsync(async ct =>
        {
            var env = CurrentStandingEnv();
            env["MESSAGING_MODE"] = mode;
            // Only link-api/redirect-api branch on this (see MessagingUsingServices's own comment) -
            // unlike every other DB-touching toggle, the 3 worker services never need recreating
            // here, since their gRPC endpoint is always listening regardless of mode.
            var scaleArgs = await runtime.BuildPreserveScaleArgsAsync(MessagingUsingServices, ct);

            logger.LogWarning("Switching Messaging__Mode for {Services} to {Mode}", string.Join(", ", MessagingUsingServices), mode);
            var (exitCode, output) = await runtime.RunComposeAsync(["up", "-d", "--force-recreate", "--no-deps", .. scaleArgs, .. MessagingUsingServices], env, ct);
            if (exitCode != 0)
            {
                logger.LogWarning("Toggling messaging mode failed (exit {ExitCode}): {Output}", exitCode, output);
                throw new InvalidOperationException($"docker compose exited {exitCode}: {output}");
            }

            _messagingMode = mode;
            return await GetInfraStatusAsync(ct);
        }, ct);
    }

    // 3 replicas only earns its keep behind a real load balancer - with haproxy down, nothing
    // round-robins across them (app services still dial haproxy:6432 specifically, not pgcat's own
    // DNS name - see SyncHaproxyContainerAsync's own comment), so they'd just sit idle. Collapsing
    // to 1 while haproxy is off and restoring 3 the moment it's back on keeps the graph's ×N badge
    // honest (no "3 replicas" badge implying load-balancing that isn't actually happening) and
    // avoids running idle replicas for no reason. Always exactly 1 or 3, never a user-chosen value -
    // unlike ScaleAsync's general mechanism, there's no pool_size recalculation risk to guard against
    // here (pgcat is deliberately excluded from ScalableServices for that reason - see this class's
    // header comment and sandbox/docs/pgcat-pool-sizing.md): fewer replicas at the same pool_size
    // only ever LOWERS total Postgres connections versus the already-proven-safe 3-replica default,
    // and restoring exactly 3 returns to that exact proven-safe state, never something new.
    private async Task SyncPgcatReplicaCountAsync(int replicas, CancellationToken ct)
    {
        var env = new Dictionary<string, string> { ["PGCAT_TOML_HOST_PATH"] = await GetOwnPgcatConfigHostPathAsync(ct) };

        // --no-deps is load-bearing here too, same reasoning as SetPgcatEnabledAsync's own comment on
        // it: without it, `up` also starts pgcat's depends_on chain (postgres-replica1/2, condition
        // service_healthy) - found live, this toggle could silently restart replicas the separate
        // "Postgres replicas" toggle had deliberately stopped.
        logger.LogWarning("Scaling pgcat to {Replicas} replica(s) (haproxy enabled: {HaproxyEnabled})", replicas, replicas > 1);
        var (exitCode, output) = await runtime.RunComposeAsync(["up", "-d", "--scale", $"pgcat={replicas}", "--no-recreate", "--no-deps", "pgcat"], env, ct);
        if (exitCode != 0)
        {
            logger.LogWarning("Scaling pgcat to {Replicas} failed (exit {ExitCode}): {Output}", replicas, exitCode, output);
            throw new InvalidOperationException($"docker compose exited {exitCode}: {output}");
        }
    }

    // Resolves pgcat.toml's real host-absolute path live, from Docker's own bookkeeping, rather
    // than hardcoding or requiring a manually-configured env var - works on any host OS/drive
    // letter/checkout location without extra setup. HOSTNAME is the standard Docker convention
    // (a container's hostname defaults to its own short container ID unless overridden, and
    // nothing in this compose file gives control-api a custom one), so inspecting "ourselves" by
    // that id is reliable. Reuses the already-existing /pgcat-config mount (the same one
    // SetPgcatPoolSettingsAsync writes pgcat.toml through) rather than /workspace - Docker's own
    // Mounts metadata records that mount's real Source path directly, with no "which subdirectory
    // under the repo root" arithmetic needed on top.
    private async Task<string> GetOwnPgcatConfigHostPathAsync(CancellationToken ct)
    {
        var selfId = Environment.GetEnvironmentVariable("HOSTNAME")
            ?? throw new InvalidOperationException("HOSTNAME is not set - cannot resolve this container's own identity to inspect its mounts.");
        var self = await runtime.Client.Containers.InspectContainerAsync(selfId, ct);
        var mount = self.Mounts.FirstOrDefault(m => m.Destination == "/pgcat-config")
            ?? throw new InvalidOperationException("This container has no /pgcat-config mount - cannot resolve pgcat.toml's real host path.");
        return $"{mount.Source}/pgcat.toml";
    }

    // DB_HOST/DB_PORT stay pointed at "haproxy" the whole time this toggles (see InfraStatus's own
    // comment on HaproxyEnabled), so stopping it while pgcat routing is still live genuinely takes
    // the pooler layer's single entry point down, same as degrading/stopping any other node here.
    // Only legal to turn ON while pgcat is enabled - SetPgcatEnabledAsync(false) already forces this
    // off as a side effect (nothing routes through haproxy once pgcat is bypassed), so allowing a
    // standalone "turn it back on" here while pgcat stays off would just leave a load balancer in
    // front of nothing. Turning it OFF is always allowed, including while pgcat is already off, so
    // SetPgcatEnabledAsync and this method never fight over a legal value. Shares the same exclusive
    // gate as the env-rewriting toggles purely for consistency - nothing else here reads or writes
    // haproxy's container state outside that gate, so there's no actual race to prevent.
    public Task<InfraStatus> SetHaproxyEnabledAsync(bool enabled, CancellationToken ct) =>
        runtime.RunExclusiveToggleAsync(async ct =>
        {
            if (enabled && !_pgcatEnabled)
            {
                throw new InvalidOperationException("Cannot turn haproxy on while connection pooling (pgcat) is disabled - nothing routes through it.");
            }

            await SyncHaproxyContainerAsync(enabled, ct);
            // See SyncPgcatReplicaCountAsync's own comment - symmetric with this toggle itself
            // (unlike the one-directional pgcat->haproxy cascade above), since both directions here
            // just restore or collapse a count that's always meant to track haproxy's own state.
            await SyncPgcatReplicaCountAsync(enabled ? 3 : 1, ct);
            return await GetInfraStatusAsync(ct);
        }, ct);

    // Shared by SetPgcatEnabledAsync (one-directional: only ever calls this with false, to force
    // haproxy off alongside pgcat) and SetHaproxyEnabledAsync (the standalone sub-toggle, which
    // calls this with either value). Doesn't acquire RunExclusiveToggleAsync's gate itself, so
    // SetPgcatEnabledAsync can call it from inside its own already-gated delegate without a nested
    // (and likely non-reentrant) acquire. Doesn't write back to any field either - GetInfraStatusAsync
    // reads haproxy's container state live (see InfraStatus's own comment on HaproxyEnabled), so
    // there's nothing here to keep in sync.
    private async Task SyncHaproxyContainerAsync(bool enabled, CancellationToken ct)
    {
        var container = await runtime.FindAsync("haproxy", ct)
            ?? throw new InvalidOperationException("haproxy container not found");

        logger.LogWarning("Switching haproxy container to {Enabled}", enabled);
        if (enabled)
        {
            await runtime.Client.Containers.StartContainerAsync(container.ID, new ContainerStartParameters(), ct);
        }
        else
        {
            await runtime.Client.Containers.StopContainerAsync(container.ID, new ContainerStopParameters { WaitBeforeKillSeconds = 10 }, ct);
        }
    }

    // Stop (not remove) postgres-replica1/2 - their volumes and the permanent replication slot
    // replica-entrypoint.sh creates via `pg_basebackup -C --slot=...` survive a stop, so a later ON
    // resumes streaming from where it left off instead of re-running a full base backup. Ordering
    // is deliberately asymmetric around pgcat.toml's servers list (see SetPostgresReplicasEnabledAsync):
    // this method only ever touches the containers themselves.
    //
    // No --force-recreate/docker compose involved at all, unlike every other toggle in this class -
    // these two are plain named containers with no shared env vars or connection-string flip, so a
    // stop/start on their existing container ID is sufficient and keeps everything (hostname,
    // volume, replication slot) exactly as it was.
    private async Task SyncPostgresReplicaContainersAsync(bool enabled, CancellationToken ct)
    {
        foreach (var name in PostgresReplicaContainers)
        {
            var container = await runtime.FindAsync(name, ct)
                ?? throw new InvalidOperationException($"{name} container not found");

            logger.LogWarning("Switching {Name} container to {Enabled}", name, enabled);
            if (enabled)
            {
                await runtime.Client.Containers.StartContainerAsync(container.ID, new ContainerStartParameters(), ct);
            }
            else
            {
                await runtime.Client.Containers.StopContainerAsync(container.ID, new ContainerStopParameters { WaitBeforeKillSeconds = 10 }, ct);
            }
        }
    }

    // OFF drops the two replicas from pgcat.toml's servers list BEFORE stopping their containers -
    // pgcat.SetReplicasEnabledAsync issues a best-effort immediate admin-console RELOAD on every
    // pgcat instance right after the file write (see its own comment), closing the window down to
    // effectively zero in the normal case instead of leaving it open for pgcat's own
    // `autoreload = 15000` timer to notice up to ~15s later. ON is the mirror image: start the
    // containers first, then add them back to pgcat's servers list - pgcat's own healthcheck_delay
    // already gates real traffic until a freshly (re)started replica answers, so there's no window
    // where pgcat routes to something not listening yet.
    public Task<InfraStatus> SetPostgresReplicasEnabledAsync(bool enabled, CancellationToken ct) =>
        runtime.RunExclusiveToggleAsync(async ct =>
        {
            if (enabled)
            {
                await SyncPostgresReplicaContainersAsync(true, ct);
                await pgcat.SetReplicasEnabledAsync(true, ct);
            }
            else
            {
                await pgcat.SetReplicasEnabledAsync(false, ct);
                await SyncPostgresReplicaContainersAsync(false, ct);
            }

            return await GetInfraStatusAsync(ct);
        }, ct);

    // Starts/stops redis-master AND, in lockstep, Cache__Enabled on link-api/redirect-api - unlike
    // every other cascade in this class, these two aren't "parent forces a dependent off/leaves it
    // for the user to re-enable" (pgcat->haproxy, replicas->pgcat.toml): a redis-master nobody's
    // allowed to read from would be a node on the graph with no reason to exist, so ON and OFF both
    // move CacheEnabled in lockstep with RedisEnabled, not just OFF. Sentinel is the one real
    // cascade here - OFF forces it off too (nothing left to fail over to), ON does NOT restore it
    // (bringing up the HA layer is still its own explicit action, same rule as every other toggle in
    // this class) - see SetRedisSentinelEnabledAsync and InfraStatus's own comment on RedisEnabled.
    public Task<InfraStatus> SetRedisEnabledAsync(bool enabled, CancellationToken ct) =>
        runtime.RunExclusiveToggleAsync(async ct =>
        {
            if (enabled)
            {
                await SyncRedisMasterContainerAsync(true, ct);
                // redis-master's entrypoint always bakes in min-replicas-to-write=1 on every restart
                // (a CLI flag, never persisted to disk) regardless of whether Sentinel/replicas are
                // actually up - found live: starting Redis back up while Sentinel was still off
                // (REDIS_CONN still pointed directly at redis-master, no replica connected)
                // reintroduced the exact NOREPLICAS write-fencing bug SetRedisSentinelEnabledAsync's
                // own min-replicas-to-write handling already fixes, since nothing here re-synced this
                // fresh container back to the toggle's actual current state. Match it to the current
                // REDIS_CONN form instead of trusting the container's own just-applied default.
                await SetRedisMinReplicasToWriteAsync(_redisConnectionViaSentinel ? 1 : 0, ct);
                _cacheEnabled = true;
                await RecreateCacheUsingServicesAsync(ct);
            }
            else
            {
                // Set _cacheEnabled before the recreate below so its own CurrentStandingEnv() read
                // already carries CACHE_ENABLED=false - folds both axes into the ONE recreate pass
                // SetRedisSentinelEnabledInternalAsync(false, ...) already needs for REDIS_CONN,
                // instead of a second, redundant force-recreate of the same services right after.
                _cacheEnabled = false;
                await SetRedisSentinelEnabledInternalAsync(false, ct);
                await SyncRedisMasterContainerAsync(false, ct);
            }

            return await GetInfraStatusAsync(ct);
        }, ct);

    // Only legal to turn on while Redis itself is on (checked live, not against a cached field -
    // RedisEnabled has no cached counterpart, unlike PgcatEnabled/HaproxyEnabled's pair) - same
    // "can't monitor/fail over to nothing" reasoning as HaproxyEnabled requiring PgcatEnabled.
    public Task<InfraStatus> SetRedisSentinelEnabledAsync(bool enabled, CancellationToken ct) =>
        runtime.RunExclusiveToggleAsync(async ct =>
        {
            if (enabled && !await IsRedisMasterRunningAsync(ct))
            {
                throw new InvalidOperationException("Cannot enable Redis Sentinel while Redis itself is disabled - nothing to monitor or fail over to.");
            }

            await SetRedisSentinelEnabledInternalAsync(enabled, ct);
            return await GetInfraStatusAsync(ct);
        }, ct);

    // Shared by the public SetRedisSentinelEnabledAsync and by SetRedisEnabledAsync's OFF cascade
    // (which cannot call the public, gated method from inside its own already-gated delegate - same
    // reasoning as SyncHaproxyContainerAsync being a private helper). OFF recreates the 6 services
    // onto the direct connection string BEFORE stopping the HA containers - same "safe state first"
    // ordering as SetPostgresReplicasEnabledAsync, so no service is ever left holding a Sentinel-form
    // connection string pointed at Sentinels that are about to disappear. ON is the mirror image:
    // start the HA containers first, then recreate the 6 services onto the Sentinel-form string.
    //
    // redis-master's own min-replicas-to-write (redis-node-entrypoint.sh's MIN_REPLICAS_FLAGS, the
    // split-brain mitigation from the 2026-09-22 resilience pass) is a second piece of state this
    // toggle has to carry, found by actually exercising this toggle live: with the 2 replicas
    // stopped, redis-master still demanded >=1 connected replica before any write, so even a
    // perfectly-correct direct connection string just got NOREPLICAS errors on every cache write -
    // relaxing it to 0 going OFF and restoring it to 1 going ON (both live via CONFIG SET, no
    // restart) is what actually makes "Redis on, Sentinel off" a working standalone cache instead of
    // a write-fenced one. OFF relaxes it FIRST, before anything else, so the services the very next
    // step recreates never race a master that's still fencing; ON restores it LAST, after the
    // replicas have had the whole recreate-the-6-services step's worth of time to reconnect - not a
    // guarantee, but the same "good enough in practice, not worth a polling wait" tolerance pgcat's
    // ban_time/healthcheck_delay already apply elsewhere in this class.
    //
    // A third piece of state, also found by exercising this live: redis-sentinel-1/2/3's own
    // entrypoint (docker-compose.yml's inline command, not redis-node-entrypoint.sh) keeps
    // sentinel.conf exactly as-is on every restart once it's ever carried a "# Generated by CONFIG
    // REWRITE" marker - unlike the data nodes' own entrypoint, it never re-derives "mymaster" via DNS
    // again. This toggle is the first thing in this stack's normal lifecycle that actually restarts
    // the Sentinel containers (previously only a full `docker compose` stack restart or genuine
    // chaos-testing did), which surfaces exactly the staleness RedisInfraService.SelfHealSentinelAsync
    // already exists to correct (see its own comment for the original incident) - but only once that
    // routine's next 30s poll tick runs, and only if at least one data node is currently reachable
    // and reporting ROLE=master for it to anchor on. Confirmed live: without an explicit correction
    // right here, a Sentinel restart with stale config can fail over onto a now-dead address and tell
    // the real redis-master to become its replica, and - since this toggle's own ON path is the only
    // thing that ever starts these 5 containers back up - nothing else in the system would un-stick
    // that until someone intervenes by hand. ReclaimRedisMasterRoleAsync runs FIRST, before the HA
    // containers even start, so there is an unambiguous, confirmable master for both the freshly
    // (re)started replicas' own entrypoints and SelfHealSentinelAsync's ground-truth ROLE check to
    // anchor on - SelfHealSentinelAsync itself then forces every Sentinel's belief to match it
    // immediately, rather than leaving that to chance or a 30s-away poll tick.
    private async Task SetRedisSentinelEnabledInternalAsync(bool enabled, CancellationToken ct)
    {
        if (enabled)
        {
            await ReclaimRedisMasterRoleAsync(ct);
            await SyncRedisHaContainersAsync(true, ct);
            await redis.SelfHealSentinelAsync(ct);
            _redisConnectionViaSentinel = true;
            await RecreateRedisUsingServicesAsync(ct);
            await SetRedisMinReplicasToWriteAsync(1, ct);
        }
        else
        {
            await SetRedisMinReplicasToWriteAsync(0, ct);
            _redisConnectionViaSentinel = false;
            await RecreateRedisUsingServicesAsync(ct);
            await SyncRedisHaContainersAsync(false, ct);
        }
    }

    // Best-effort overall, same reasoning as PgcatService.ReloadAllInstancesAsync: redis-master being
    // unreachable after retrying must not abort a toggle that already stopped/started real
    // containers - the entrypoint's own MIN_REPLICAS_FLAGS is still the authoritative value on
    // redis-master's next restart regardless, so an exhausted-retries failure here only means this
    // one toggle cycle ran with whichever value was already in effect.
    //
    // Retries with exit-code checking, not a single ExecAsync call - found live, right after
    // SyncRedisMasterContainerAsync(true, ...) starts the container, redis-server can take a real,
    // not-brief moment to actually accept connections: redis-node-entrypoint.sh's own Sentinel-
    // discovery attempt (getent + up to 5 retries of a 2s-spaced SENTINEL query against each of 3
    // sentinel hosts before falling back to NODE_DEFAULT_ROLE) can legitimately run ~10s before it
    // ever execs redis-server, confirmed live - a first attempt at 500ms and a short handful of
    // retries both still hit Connection refused the whole way through. 20 attempts a second apart
    // covers that worst case with headroom. ExecAsync doesn't surface a non-zero exit code (its own
    // contract is "whichever of stdout/stderr has content", not success/failure), so a `redis-cli`
    // that merely printed a connection error used to look identical to one that actually applied the
    // setting - confirmed live: the ON path's own fix for this exact method appeared to succeed in
    // the logs while min-replicas-to-write silently stayed at the entrypoint's baked-in default.
    private async Task SetRedisMinReplicasToWriteAsync(int value, CancellationToken ct)
    {
        var container = await runtime.FindAsync("redis-master", ct);
        if (container is null)
        {
            logger.LogWarning("redis-master container not found - skipping min-replicas-to-write={Value}", value);
            return;
        }

        const int maxAttempts = 20;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var (exitCode, output) = await runtime.ExecWithExitCodeAsync(container.ID, ["redis-cli", "CONFIG", "SET", "min-replicas-to-write", value.ToString()], ct);
            if (exitCode == 0)
            {
                logger.LogWarning("Set redis-master min-replicas-to-write={Value} (attempt {Attempt}/{MaxAttempts})", value, attempt, maxAttempts);
                return;
            }

            logger.LogDebug("SetRedisMinReplicasToWriteAsync: attempt {Attempt}/{MaxAttempts} failed (exit {ExitCode}): {Output}", attempt, maxAttempts, exitCode, output);
            if (attempt < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }

        logger.LogWarning("Setting redis-master min-replicas-to-write={Value} failed after {MaxAttempts} attempts", value, maxAttempts);
    }

    // `REPLICAOF NO ONE` on a node that's already master is a documented no-op, but redis-master
    // is NOT always already master at this point - if a real Sentinel failover already promoted a
    // different node before this toggle cycle ran (e.g. a previous ON/OFF/ON that left redis-master
    // demoted to a replica of that node), forcing it back to master here unconditionally would
    // create a second master instead of fixing anything: SelfHealSentinelAsync right after this can
    // only safely re-point Sentinel when exactly one node reports ROLE=master to anchor on (see
    // RedisInfraService.SelectSoleMaster) - a self-inflicted two-master state makes it see >1
    // master, give up for that tick, and leave every Sentinel stuck, possibly pointed at neither
    // real node. Found in review: checking the real topology first and skipping the reclaim when
    // some OTHER node already holds the master role avoids manufacturing exactly the ambiguous state
    // SelectSoleMaster can't resolve on its own - that other node is already the unambiguous ground
    // truth SelfHealSentinelAsync needs, nothing to reclaim.
    private async Task ReclaimRedisMasterRoleAsync(CancellationToken ct)
    {
        var container = await runtime.FindAsync("redis-master", ct);
        if (container is null)
        {
            return;
        }

        var topology = await redis.GetRedisTopologyAsync(ct);
        var otherMaster = topology.Roles.FirstOrDefault(r => r.ServiceId != "redis-master" && r.Role == "master");
        if (otherMaster is not null)
        {
            logger.LogWarning(
                "Skipping redis-master's REPLICAOF NO ONE - {OtherServiceId} is already reporting ROLE=master, so it's already the unambiguous master for SelfHealSentinelAsync to anchor on",
                otherMaster.ServiceId);
            return;
        }

        // Still safe to call unconditionally here (including when redis-master is already master
        // itself) - a failure (redis-master unreachable) is caught by SyncRedisHaContainersAsync's
        // own container-not-found check right after anyway, so swallowing it here just avoids
        // double-logging the same underlying problem.
        try
        {
            await runtime.ExecAsync(container.ID, ["redis-cli", "-p", "6379", "REPLICAOF", "NO", "ONE"], ct);
            logger.LogWarning("Reclaimed redis-master's own master role (REPLICAOF NO ONE) before bringing Sentinel back up");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Reclaiming redis-master's master role failed");
        }
    }

    private async Task RecreateRedisUsingServicesAsync(CancellationToken ct)
    {
        var env = CurrentStandingEnv();
        var scaleArgs = await runtime.BuildPreserveScaleArgsAsync(RedisUsingServices, ct);

        logger.LogWarning(
            "Switching ConnectionStrings__Redis for {Services} to {Conn} (cache enabled: {CacheEnabled})",
            string.Join(", ", RedisUsingServices), env["REDIS_CONN"], _cacheEnabled);
        var (exitCode, output) = await runtime.RunComposeAsync(["up", "-d", "--force-recreate", "--no-deps", .. scaleArgs, .. RedisUsingServices], env, ct);
        if (exitCode != 0)
        {
            logger.LogWarning("Recreating Redis-using services failed (exit {ExitCode}): {Output}", exitCode, output);
            throw new InvalidOperationException($"docker compose exited {exitCode}: {output}");
        }
    }

    private async Task SyncRedisMasterContainerAsync(bool enabled, CancellationToken ct)
    {
        var container = await runtime.FindAsync("redis-master", ct)
            ?? throw new InvalidOperationException("redis-master container not found");

        logger.LogWarning("Switching redis-master container to {Enabled}", enabled);
        if (enabled)
        {
            await runtime.Client.Containers.StartContainerAsync(container.ID, new ContainerStartParameters(), ct);
        }
        else
        {
            await runtime.Client.Containers.StopContainerAsync(container.ID, new ContainerStopParameters { WaitBeforeKillSeconds = 10 }, ct);
        }
    }

    // Stop (not remove) redis-sentinel-1/2/3 and redis-replica1/2 - same reasoning as
    // SyncPostgresReplicaContainersAsync: their own entrypoints already self-heal role/config on a
    // plain restart (Sentinel only regenerates sentinel.conf if it doesn't already carry
    // CONFIG REWRITE-learned state, replicas re-derive their role via Sentinel rather than blindly
    // rejoining as a replica - see docker-compose.yml's own comments on both), so there's nothing a
    // remove-and-recreate would buy here that stop/start doesn't already give for free.
    private async Task SyncRedisHaContainersAsync(bool enabled, CancellationToken ct)
    {
        foreach (var name in RedisHaContainers)
        {
            var container = await runtime.FindAsync(name, ct)
                ?? throw new InvalidOperationException($"{name} container not found");

            logger.LogWarning("Switching {Name} container to {Enabled}", name, enabled);
            if (enabled)
            {
                await runtime.Client.Containers.StartContainerAsync(container.ID, new ContainerStartParameters(), ct);
            }
            else
            {
                await runtime.Client.Containers.StopContainerAsync(container.ID, new ContainerStopParameters { WaitBeforeKillSeconds = 10 }, ct);
            }
        }
    }

    // OFF drops mongo2/mongo3 from rs0's own voting configuration BEFORE stopping their containers -
    // a plain stop while they're still configured members would leave mongo1 able to see only 1 of 3
    // votes (not a majority) and step down to SECONDARY, refusing writes entirely (see
    // MongoTopologyService.SetReplicasEnabledAsync's own comment) - unlike Postgres/Redis, a container
    // stop alone is actively dangerous here, not just incomplete. ON is the mirror image: start the
    // containers first (mongo2/mongo3's own `--replSet rs0` flag just makes them come up idle,
    // waiting to be told they belong to a set - no self-election risk the way Redis's Sentinel-aware
    // entrypoint has to guard against), then rs.add() each - MongoDB gates a freshly-added member's
    // own eligibility to vote/serve reads until its initial sync completes, so there's no window
    // where traffic routes to a member that isn't ready yet.
    public Task<InfraStatus> SetMongoReplicasEnabledAsync(bool enabled, CancellationToken ct) =>
        runtime.RunExclusiveToggleAsync(async ct =>
        {
            if (enabled)
            {
                await SyncMongoReplicaContainersAsync(true, ct);
                await mongo.SetReplicasEnabledAsync(true, ct);
                _mongoConnectionIncludesReplicas = true;
                await RecreateTrafficServiceMongoAsync(ct);
            }
            else
            {
                await mongo.SetReplicasEnabledAsync(false, ct);
                _mongoConnectionIncludesReplicas = false;
                await RecreateTrafficServiceMongoAsync(ct);
                await SyncMongoReplicaContainersAsync(false, ct);
            }

            return await GetInfraStatusAsync(ct);
        }, ct);

    // Mirrors SetMongoReadPreferenceAsync's own shape exactly (single-service recreate, no
    // BuildPreserveScaleArgsAsync) - traffic-service is this toggle's only consumer, same as that one.
    private async Task RecreateTrafficServiceMongoAsync(CancellationToken ct)
    {
        var env = CurrentStandingEnv();

        logger.LogWarning("Switching ConnectionStrings__Mongo host list for traffic-service to {Hosts}", env["MONGO_HOSTS"]);
        var (exitCode, output) = await runtime.RunComposeAsync(["up", "-d", "--force-recreate", "--no-deps", "traffic-service"], env, ct);
        if (exitCode != 0)
        {
            logger.LogWarning("Recreating traffic-service for Mongo host list failed (exit {ExitCode}): {Output}", exitCode, output);
            throw new InvalidOperationException($"docker compose exited {exitCode}: {output}");
        }
    }

    // Stop (not remove) mongo2/mongo3 - same reasoning as the Postgres/Redis equivalents: whatever
    // local data survives on the kept volume is a head start, not a guarantee (MongoDB's own initial
    // sync protocol decides how much of it it can actually reuse), but removing the volume on every
    // OFF would guarantee throwing it away for no reason.
    private async Task SyncMongoReplicaContainersAsync(bool enabled, CancellationToken ct)
    {
        foreach (var name in MongoReplicaContainers)
        {
            var container = await runtime.FindAsync(name, ct)
                ?? throw new InvalidOperationException($"{name} container not found");

            logger.LogWarning("Switching {Name} container to {Enabled}", name, enabled);
            if (enabled)
            {
                await runtime.Client.Containers.StartContainerAsync(container.ID, new ContainerStartParameters(), ct);
            }
            else
            {
                await runtime.Client.Containers.StopContainerAsync(container.ID, new ContainerStopParameters { WaitBeforeKillSeconds = 10 }, ct);
            }
        }
    }
}
