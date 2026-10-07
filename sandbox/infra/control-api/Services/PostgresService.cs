using ControlApi.Models;

namespace ControlApi.Services;

public sealed class PostgresService(IContainerRuntime runtime, ILogger<PostgresService> logger) : IPostgresService
{
    // The two standbys recovery_min_apply_delay can be set on - not the primary, which has no
    // concept of replay delay.
    private static readonly IReadOnlyList<string> PostgresReplicas = ["postgres-replica1", "postgres-replica2"];

    public async Task<PostgresConnectionStats?> GetPostgresConnectionsAsync(CancellationToken ct)
    {
        var container = await runtime.FindAsync("postgres", ct);
        if (container is null)
        {
            return null;
        }

        var output = await runtime.ExecAsync(container.ID,
            ["psql", "-U", "postgres", "-tAc", "SELECT datname, count(*) FROM pg_stat_activity WHERE datname IS NOT NULL GROUP BY datname"], ct);

        var byDatabase = new Dictionary<string, int>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.Split('|');
            if (f.Length == 2 && int.TryParse(f[1], out var count))
            {
                byDatabase[f[0]] = count;
            }
        }

        return new PostgresConnectionStats(byDatabase, byDatabase.Values.Sum());
    }

    // recovery_min_apply_delay is a PGC_SIGHUP GUC on a standby - ALTER SYSTEM SET + pg_reload_conf()
    // applies it live, no restart. pg_settings.setting for a GUC_UNIT_MS parameter is always the raw
    // millisecond integer with no suffix, unlike SHOW's human-formatted output ("5s", "1min", ...) -
    // reading that column instead of parsing SHOW's text is what keeps this simple.
    public async Task<ReplicationLag?> GetReplicationLagAsync(string serviceId, CancellationToken ct)
    {
        if (!PostgresReplicas.Contains(serviceId))
        {
            return null;
        }

        var container = await runtime.FindAsync(serviceId, ct);
        if (container is null)
        {
            return null;
        }

        var output = await runtime.ExecAsync(container.ID, ["psql", "-U", "postgres", "-tAc", "SELECT setting FROM pg_settings WHERE name = 'recovery_min_apply_delay'"], ct);
        return new ReplicationLag(int.TryParse(output.Trim(), out var ms) ? ms : 0);
    }

    public async Task<ReplicationLag?> SetReplicationLagAsync(string serviceId, int delayMs, CancellationToken ct)
    {
        if (!PostgresReplicas.Contains(serviceId))
        {
            return null;
        }

        var container = await runtime.FindAsync(serviceId, ct);
        if (container is null)
        {
            return null;
        }

        // Two separate exec calls, not one "ALTER SYSTEM SET ...; SELECT pg_reload_conf();" - psql
        // sends a multi-statement -c string as one simple-query message, which Postgres runs inside
        // an implicit transaction block, and ALTER SYSTEM refuses to run inside one (confirmed live:
        // "ERROR: ALTER SYSTEM cannot run inside a transaction block" when combined).
        var (alterExitCode, alterOutput) = await runtime.ExecWithExitCodeAsync(container.ID, ["psql", "-U", "postgres", "-c", $"ALTER SYSTEM SET recovery_min_apply_delay = '{delayMs}ms'"], ct);
        if (alterExitCode != 0)
        {
            logger.LogWarning("Setting {ServiceId} replication lag failed (exit {ExitCode}): {Output}", serviceId, alterExitCode, alterOutput);
            throw new InvalidOperationException($"psql exited {alterExitCode}: {alterOutput}");
        }

        var (reloadExitCode, reloadOutput) = await runtime.ExecWithExitCodeAsync(container.ID, ["psql", "-U", "postgres", "-c", "SELECT pg_reload_conf()"], ct);
        if (reloadExitCode != 0)
        {
            logger.LogWarning("Reloading {ServiceId} config after setting replication lag failed (exit {ExitCode}): {Output}", serviceId, reloadExitCode, reloadOutput);
            throw new InvalidOperationException($"psql exited {reloadExitCode}: {reloadOutput}");
        }

        logger.LogWarning("Set {ServiceId} replication lag (recovery_min_apply_delay) to {DelayMs}ms", serviceId, delayMs);
        return new ReplicationLag(delayMs);
    }
}
