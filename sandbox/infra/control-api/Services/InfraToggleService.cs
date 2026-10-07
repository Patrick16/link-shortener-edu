using ControlApi.Models;

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
public class InfraToggleService(IContainerRuntime runtime, ILogger<InfraToggleService> logger) : IInfraToggleService
{
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

    // The two RabbitMQ consumers - workers that each run a LinkCreatedConsumer and/or
    // ClickTrackedConsumer, all sharing RabbitMqConsumer's own _prefetchCount field.
    private static readonly IReadOnlyList<string> RabbitMqConsumingServices = ["shortener-service", "traffic-service"];

    // In-memory standing toggle state - see InfraStatus for why this is fine for a local sandbox
    // tool despite not surviving a control-api restart.
    private bool _nginxBypassed;
    private bool _pgcatEnabled = true;
    private bool _cacheEnabled = true;
    private string _messagingMode = "rabbitmq";

    // Mirrors RabbitMqConsumer's own hardcoded-turned-configurable default.
    private int _rabbitMqPrefetch = 10;

    // Mirrors the shipped default in docker-compose.yml's ConnectionStrings__Mongo.
    private string _mongoReadPreference = "primary";

    // Mirrors Npgsql's own default (and the shipped ${NPGSQL_MAX_POOL_SIZE:-100} fallback).
    private int _npgsqlPoolSize = 100;

    // Every axis below recreates a service `docker compose up --force-recreate` can also touch via
    // a DIFFERENT toggle - see this class's own header comment. Every handler merges this full
    // current state and overrides only its own key before recreating, so triggering any one toggle
    // re-applies every other toggle's current value too instead of quietly undoing it.
    internal Dictionary<string, string> CurrentStandingEnv() => new()
    {
        ["DB_HOST"] = _pgcatEnabled ? "pgcat" : "postgres",
        ["DB_PORT"] = _pgcatEnabled ? "6432" : "5432",
        ["CACHE_ENABLED"] = _cacheEnabled ? "true" : "false",
        ["MESSAGING_MODE"] = _messagingMode,
        ["NPGSQL_MAX_POOL_SIZE"] = _npgsqlPoolSize.ToString(),
        ["RABBITMQ_PREFETCH"] = _rabbitMqPrefetch.ToString(),
        ["MONGO_READ_PREFERENCE"] = _mongoReadPreference,
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

    public InfraStatus GetInfraStatus() => new(_nginxBypassed, _pgcatEnabled, _cacheEnabled, _messagingMode);

    public InfraStatus SetNginxBypass(bool bypassed)
    {
        logger.LogWarning("Nginx bypass for load-test traffic: {Bypassed}", bypassed);
        _nginxBypassed = bypassed;
        return GetInfraStatus();
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
            env["DB_HOST"] = enabled ? "pgcat" : "postgres";
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
            return GetInfraStatus();
        }, ct);

    public Task<InfraStatus> SetCacheEnabledAsync(bool enabled, CancellationToken ct) =>
        runtime.RunExclusiveToggleAsync(async ct =>
        {
            var env = CurrentStandingEnv();
            env["CACHE_ENABLED"] = enabled ? "true" : "false";
            var scaleArgs = await runtime.BuildPreserveScaleArgsAsync(CacheUsingServices, ct);

            logger.LogWarning("Switching Cache__Enabled for {Services} to {Enabled}", string.Join(", ", CacheUsingServices), enabled);
            var (exitCode, output) = await runtime.RunComposeAsync(["up", "-d", "--force-recreate", "--no-deps", .. scaleArgs, .. CacheUsingServices], env, ct);
            if (exitCode != 0)
            {
                logger.LogWarning("Toggling cache failed (exit {ExitCode}): {Output}", exitCode, output);
                throw new InvalidOperationException($"docker compose exited {exitCode}: {output}");
            }

            _cacheEnabled = enabled;
            return GetInfraStatus();
        }, ct);

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
            return GetInfraStatus();
        }, ct);
    }
}
