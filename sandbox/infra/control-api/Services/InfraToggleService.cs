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
        // "haproxy" (not "pgcat") since the Pooler Scaling feature (2026-10-09) - haproxy is the
        // single entry point in front of the 3 pgcat instances now, see docker-compose.yml.
        ["DB_HOST"] = _pgcatEnabled ? "haproxy" : "postgres",
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

    // See InfraStatus's own comment on HaproxyEnabled for why that one field is read live here
    // instead of joining _nginxBypassed/_pgcatEnabled/_cacheEnabled/_messagingMode as cached state.
    public async Task<InfraStatus> GetInfraStatusAsync(CancellationToken ct) =>
        new(_nginxBypassed, _pgcatEnabled, _cacheEnabled, _messagingMode, await IsHaproxyRunningAsync(ct));

    private async Task<bool> IsHaproxyRunningAsync(CancellationToken ct) =>
        await runtime.FindAsync("haproxy", ct) is { State: "running" };

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
            // themselves. Calls the un-gated helper directly, not SetHaproxyEnabledAsync itself -
            // this whole delegate already runs inside RunExclusiveToggleAsync's gate, which isn't
            // reentrant.
            if (!enabled)
            {
                await SyncHaproxyContainerAsync(false, ct);
            }

            return await GetInfraStatusAsync(ct);
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
            return await GetInfraStatusAsync(ct);
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

        logger.LogWarning("Scaling pgcat to {Replicas} replica(s) (haproxy enabled: {HaproxyEnabled})", replicas, replicas > 1);
        var (exitCode, output) = await runtime.RunComposeAsync(["up", "-d", "--scale", $"pgcat={replicas}", "--no-recreate", "pgcat"], env, ct);
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
}
