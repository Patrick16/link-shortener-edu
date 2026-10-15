using System.Globalization;
using System.Text;
using ControlApi.Models;

namespace ControlApi.Services;

public sealed class PgcatService : IPgcatService
{
    private readonly IContainerRuntime _runtime;
    private readonly ILogger<PgcatService> _logger;
    private readonly string _pgcatConfigDir;

    private static readonly IReadOnlyList<string> PgcatDatabases = ["users_db", "links_db", "clicks_db"];

    // Mirrors pgcat.toml's shipped defaults. Raised from the original 10 after a 200-VU/20-replica
    // load test showed pgcat's pool - not the app or RabbitMQ - as the actual bottleneck: pool_size
    // didn't scale with replica count, so requests queued for 10+ seconds behind it. Lowered from
    // 40 to 20 when the Pooler Scaling feature (2026-10-09) went from 1 pgcat instance to 3 behind
    // haproxy - 40 was sized for one instance's worst case against Postgres's max_connections=200;
    // 3 instances at the old value could have opened up to 360 real connections. See
    // sandbox/docs/pgcat-pool-sizing.md.
    private PgcatPoolSettings _pgcatPoolSettings = new("transaction", true, 20);

    // Mirrors the stack's default running state (both replicas up) - see PostgresReplicasToggleCapability.
    private bool _replicasEnabled = true;

    public PgcatService(IConfiguration configuration, IContainerRuntime runtime, ILogger<PgcatService> logger)
    {
        _runtime = runtime;
        _logger = logger;
        _pgcatConfigDir = configuration["Pgcat:ConfigDir"] ?? "/pgcat-config";
    }

    // `SHOW POOLS` columns (unaligned, pipe-separated): database|user|pool_mode|cl_idle|cl_active|
    // cl_waiting|cl_cancel_req|sv_active|sv_idle|sv_used|sv_tested|sv_login|maxwait|maxwait_us -
    // confirmed against a real pgcat before parsing anything.
    //
    // pgcat is 3 identical replicas behind haproxy (Pooler Scaling, deploy.replicas in
    // docker-compose.yml) - all 3 share the compose service label "pgcat", so this queries every
    // one of them (via IContainerRuntime.ListAsync, not FindAsync's "just the primary") and labels
    // each result by its container-number ("pgcat-1"/"pgcat-2"/"pgcat-3", matching the container's
    // own real name) rather than returning just one instance's numbers.
    public async Task<IReadOnlyList<PgcatInstanceConnections>> GetAllPgcatConnectionsAsync(CancellationToken ct)
    {
        var containers = await _runtime.ListAsync("pgcat", ct);
        var result = new List<PgcatInstanceConnections>();
        foreach (var container in containers)
        {
            var number = container.Labels.TryGetValue("com.docker.compose.container-number", out var n) ? n : "?";
            var stats = await GetPoolStatsAsync(container.ID, ct);
            result.Add(new PgcatInstanceConnections($"pgcat-{number}", stats));
        }

        return result;
    }

    private async Task<PgcatConnectionStats> GetPoolStatsAsync(string containerId, CancellationToken ct)
    {
        var output = await _runtime.ExecAsync(containerId, ["sh", "-c", "PGPASSWORD=admin_pass psql -h 127.0.0.1 -p 6432 -U admin_user pgcat -tAc \"SHOW POOLS\""], ct);
        var pools = new List<PoolConnectionStats>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.Split('|');
            if (f.Length < 10)
            {
                continue;
            }

            pools.Add(new PoolConnectionStats(
                f[0],
                int.Parse(f[3], CultureInfo.InvariantCulture),
                int.Parse(f[4], CultureInfo.InvariantCulture),
                int.Parse(f[5], CultureInfo.InvariantCulture),
                int.Parse(f[7], CultureInfo.InvariantCulture),
                int.Parse(f[8], CultureInfo.InvariantCulture),
                int.Parse(f[9], CultureInfo.InvariantCulture)));
        }

        return new PgcatConnectionStats(pools);
    }

    public PgcatPoolSettings GetPgcatPoolSettings() => _pgcatPoolSettings;

    // Rewrites pgcat.toml wholesale rather than patching the existing file's text - deterministic
    // and reversible regardless of what the file currently looks like. Written to a *separate*
    // writable bind mount of the same host directory (control-api's own workspace mount is
    // read-only); pgcat itself still mounts the file read-only, but Docker bind mounts are live
    // views of the same host file. WritePgcatTomlAsync also issues an immediate admin-console
    // `RELOAD` on every instance (see its own comment) rather than relying on pgcat's own
    // `autoreload = 15000` timer to notice - no docker compose call either way, no recreate, no
    // cascade risk. Collapses the original file's differentiated per-pool sizes into one shared
    // value - acceptable for an experimental control whose whole point is "what happens if I shrink
    // every pool", not preserving the original tuning.
    public async Task<PgcatPoolSettings> SetPgcatPoolSettingsAsync(PgcatPoolSettings settings, CancellationToken ct)
    {
        await WritePgcatTomlAsync(settings, _replicasEnabled, ct);

        _logger.LogWarning(
            "Rewrote pgcat.toml: pool_mode={PoolMode}, read_write_splitting={ReadWriteSplitting}, pool_size={PoolSize} - reloaded on every instance",
            settings.PoolMode, settings.ReadWriteSplitting, settings.PoolSize);

        _pgcatPoolSettings = settings;
        return settings;
    }

    // Keeps _pgcatPoolSettings (pool_mode/read-write-splitting/pool_size) exactly as they are -
    // only the `servers` list each pool's [shards.0] carries changes. Shares the same
    // rewrite-wholesale-and-reload mechanism as SetPgcatPoolSettingsAsync, not a docker compose
    // recreate - postgres-replica1/2 themselves are stopped/started separately by
    // InfraToggleService, around this call. The immediate RELOAD (not just the file write) is what
    // actually makes InfraToggleService's OFF-then-stop / ON-then-start ordering safe - a replica
    // dropped here must stop being a pgcat target before (not up to 15s after) its container stops,
    // and one added back must become a pgcat target as soon as its container is actually up.
    public async Task SetReplicasEnabledAsync(bool enabled, CancellationToken ct)
    {
        await WritePgcatTomlAsync(_pgcatPoolSettings, enabled, ct);

        _logger.LogWarning(
            "Rewrote pgcat.toml: replicas {Enabled} - reloaded on every instance",
            enabled ? "included" : "excluded");

        _replicasEnabled = enabled;
    }

    private async Task WritePgcatTomlAsync(PgcatPoolSettings settings, bool replicasEnabled, CancellationToken ct)
    {
        var path = Path.Combine(_pgcatConfigDir, "pgcat.toml");
        await File.WriteAllTextAsync(path, RenderPgcatToml(settings, replicasEnabled), ct);
        await ReloadAllInstancesAsync(ct);
    }

    // `RELOAD` is pgcat's own admin-console command (same `psql`-over-the-admin-port channel
    // GetPoolStatsAsync already uses for `SHOW POOLS`) to pick up pgcat.toml immediately - confirmed
    // live against a running instance. Without this, the file write alone only takes effect whenever
    // pgcat's own `autoreload = 15000` timer next fires (up to ~15s later), which is exactly the
    // race a caller relying on "the write already happened" (see SetReplicasEnabledAsync's own
    // comment) needs closed. Every one of the 3 instances needs its own RELOAD - each has an
    // independent in-memory config despite sharing the one mounted file.
    //
    // Best-effort, not fail-the-whole-toggle: a transient exec failure against one instance (or
    // Docker being briefly unreachable, or - in a unit test with no "pgcat" containers at all -
    // ListAsync legitimately returning none) must not leave SetReplicasEnabledAsync's caller
    // thinking the file write itself failed. pgcat's own `autoreload` timer is still running
    // regardless, so a swallowed failure here only widens the window back to "at most ~15s" instead
    // of leaving pgcat permanently out of sync.
    private async Task ReloadAllInstancesAsync(CancellationToken ct)
    {
        List<Docker.DotNet.Models.ContainerListResponse> containers;
        try
        {
            containers = await _runtime.ListAsync("pgcat", ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Listing pgcat instances to reload failed - pgcat.toml was still rewritten, autoreload will pick it up within ~15s");
            return;
        }

        foreach (var container in containers)
        {
            try
            {
                await _runtime.ExecAsync(container.ID, ["sh", "-c", "PGPASSWORD=admin_pass psql -h 127.0.0.1 -p 6432 -U admin_user pgcat -c RELOAD"], ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reloading pgcat instance {ContainerId} failed - autoreload will pick it up within ~15s", container.ID);
            }
        }
    }

    private static string RenderPgcatToml(PgcatPoolSettings settings, bool replicasEnabled)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Rewritten live by control-api's pgcat pool-settings control (see PgcatService.SetPgcatPoolSettingsAsync).");
        sb.AppendLine(replicasEnabled
            ? "# Primary + 2 streaming replicas (postgres-replica1/2), same trio for all three databases."
            : "# Primary only - replicas excluded (see PgcatService.SetReplicasEnabledAsync).");
        sb.AppendLine("# This ONE file is mounted read-only into all 3 pgcat instances (pgcat-1/2/3) behind haproxy.");
        sb.AppendLine();
        sb.AppendLine("[general]");
        sb.AppendLine("host = \"0.0.0.0\"");
        sb.AppendLine("port = 6432");
        // Scraped by Prometheus's pgcat job (see sandbox/infra/prometheus/prometheus.yml) for the
        // PgcatClientsWaiting alert (pgcat_pools_cl_waiting - see sandbox/infra/prometheus/alerts.yml).
        // 9930 is pgcat's own documented default for this exporter, not configurable from here.
        sb.AppendLine("enable_prometheus_exporter = true");
        sb.AppendLine("prometheus_exporter_port = 9930");
        sb.AppendLine("connect_timeout = 5000");
        sb.AppendLine("idle_timeout = 30000");
        sb.AppendLine("healthcheck_timeout = 1000");
        sb.AppendLine("healthcheck_delay = 30000");
        sb.AppendLine("shutdown_timeout = 5000");
        // Was 20 (seconds) - shortened after a Docker-embedded-DNS flake against a replica turned
        // into a ~20s ban that magnified a sub-second blip into real p95/max latency on live
        // traffic. See sandbox/docs/pgcat-pool-sizing.md.
        sb.AppendLine("ban_time = 3");
        sb.AppendLine("log_client_connections = false");
        sb.AppendLine("log_client_disconnections = false");
        sb.AppendLine("autoreload = 15000");
        sb.AppendLine("worker_threads = 4");
        sb.AppendLine("admin_username = \"admin_user\"");
        sb.AppendLine("admin_password = \"admin_pass\"");

        foreach (var database in PgcatDatabases)
        {
            sb.AppendLine();
            sb.AppendLine($"[pools.{database}]");
            sb.AppendLine($"pool_mode = \"{settings.PoolMode}\"");
            sb.AppendLine("default_role = \"primary\"");
            sb.AppendLine("query_parser_enabled = true");
            sb.AppendLine($"query_parser_read_write_splitting = {(settings.ReadWriteSplitting ? "true" : "false")}");
            sb.AppendLine("primary_reads_enabled = true");
            sb.AppendLine();
            sb.AppendLine($"[pools.{database}.users.0]");
            sb.AppendLine("username = \"postgres\"");
            sb.AppendLine("password = \"postgres\"");
            sb.AppendLine($"pool_size = {settings.PoolSize}");
            sb.AppendLine("statement_timeout = 0");
            sb.AppendLine();
            sb.AppendLine($"[pools.{database}.shards.0]");
            sb.AppendLine(replicasEnabled
                ? "servers = [[\"postgres\", 5432, \"primary\"], [\"postgres-replica1\", 5432, \"replica\"], [\"postgres-replica2\", 5432, \"replica\"]]"
                : "servers = [[\"postgres\", 5432, \"primary\"]]");
            sb.AppendLine($"database = \"{database}\"");
        }

        return sb.ToString();
    }
}
