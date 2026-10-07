using ControlApi.Models;
using Docker.DotNet.Models;

namespace ControlApi.Services;

public sealed class RedisInfraService(IContainerRuntime runtime, ILogger<RedisInfraService> logger) : IRedisInfraService
{
    // SENTINEL SET is local to whichever Sentinel instance receives it - not gossiped to the other
    // two - so every config change loops over all three to keep them in sync.
    private static readonly IReadOnlyList<string> SentinelContainers = ["redis-sentinel-1", "redis-sentinel-2", "redis-sentinel-3"];

    // The three Redis data-plane containers whose actual replication role can change on its own via
    // Sentinel failover, independent of anything this app does.
    private static readonly IReadOnlyList<string> RedisDataNodes = ["redis-master", "redis-replica1", "redis-replica2"];

    public async Task<string?> FlushRedisAsync(CancellationToken ct)
    {
        // FLUSHALL against the master alone is enough - Redis propagates it to redis-replica1/2 via
        // normal command replication, no need to flush each node separately.
        var container = await runtime.FindAsync("redis-master", ct);
        if (container is null)
        {
            return null;
        }

        var output = await runtime.ExecAsync(container.ID, ["redis-cli", "FLUSHALL"], ct);
        logger.LogWarning("Flushed Redis cache: {Output}", output.Trim());
        return output.Trim();
    }

    // Redis Sentinel can fail over on its own - "redis-master" is only a hostname/label in
    // architecture.json, not a guarantee that container is still the one actually serving writes.
    // ROLE is asked of each of the three data nodes directly, not read off Sentinel's own view - it's
    // the ground truth of what each Redis process itself currently believes it is.
    public async Task<InfraTopology> GetRedisTopologyAsync(CancellationToken ct)
    {
        var roles = new List<NodeRole>();
        foreach (var serviceId in RedisDataNodes)
        {
            var container = await runtime.FindAsync(serviceId, ct);
            if (container is null)
            {
                roles.Add(new NodeRole(serviceId, "unreachable"));
                continue;
            }

            try
            {
                var output = await runtime.ExecAsync(container.ID, ["redis-cli", "-p", "6379", "ROLE"], ct);
                var role = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                roles.Add(new NodeRole(serviceId, role switch { "master" => "master", "slave" => "replica", _ => "unreachable" }));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to read Redis ROLE from {ServiceId}", serviceId);
                roles.Add(new NodeRole(serviceId, "unreachable"));
            }
        }

        return new InfraTopology(roles);
    }

    // SENTINEL MASTER returns a flat alternating key/value list (confirmed against a real Sentinel
    // before parsing anything) - reads it off redis-sentinel-1 as a representative instance, since a
    // successful SetSentinelConfigAsync keeps all three in sync anyway.
    public async Task<SentinelConfig?> GetSentinelConfigAsync(CancellationToken ct)
    {
        var container = await runtime.FindAsync("redis-sentinel-1", ct);
        if (container is null)
        {
            return null;
        }

        var output = await runtime.ExecAsync(container.ID, ["redis-cli", "-p", "26379", "SENTINEL", "MASTER", "mymaster"], ct);
        var lines = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var fields = new Dictionary<string, string>();
        for (var i = 0; i + 1 < lines.Length; i += 2)
        {
            fields[lines[i]] = lines[i + 1];
        }

        int Get(string key) => fields.TryGetValue(key, out var v) && int.TryParse(v, out var n) ? n : 0;
        return new SentinelConfig(Get("down-after-milliseconds"), Get("quorum"), Get("failover-timeout"));
    }

    public async Task<SentinelConfig> SetSentinelConfigAsync(SentinelConfig config, CancellationToken ct)
    {
        foreach (var serviceId in SentinelContainers)
        {
            var container = await runtime.FindAsync(serviceId, ct);
            if (container is null)
            {
                // A missing container used to just `continue` past, then still return `config` at
                // the end as if it had been applied to all three - the caller had no way to tell a
                // partial application (e.g. one Sentinel down for maintenance) from a full one.
                // Failing loudly here is the same "don't report success you didn't verify" principle
                // as the exit-code checks below, just for "the container wasn't even there" instead
                // of "the command it ran failed".
                logger.LogWarning("Setting Sentinel config failed: {ServiceId} container not found", serviceId);
                throw new InvalidOperationException($"Sentinel container {serviceId} not found");
            }

            // Three separate SENTINEL SET calls, not one with multiple option/value pairs - kept to
            // exactly the shape already confirmed live against a real Sentinel, one option at a time.
            await SentinelSetAsync(container.ID, serviceId, "down-after-milliseconds", config.DownAfterMs.ToString(), ct);
            await SentinelSetAsync(container.ID, serviceId, "quorum", config.Quorum.ToString(), ct);
            await SentinelSetAsync(container.ID, serviceId, "failover-timeout", config.FailoverTimeoutMs.ToString(), ct);
        }

        logger.LogWarning(
            "Set Sentinel config on {Containers}: down-after-milliseconds={DownAfterMs}, quorum={Quorum}, failover-timeout={FailoverTimeoutMs}",
            string.Join(", ", SentinelContainers), config.DownAfterMs, config.Quorum, config.FailoverTimeoutMs);

        return config;
    }

    // redis-cli exits non-zero not just on a connection failure but also when the server's reply to
    // the command it ran was itself an error (e.g. "(error) ERR ..."), so this exit code is a real
    // signal here - unlike a bare docker-exec-couldn't-start failure, it's specifically the SENTINEL
    // SET command that Redis itself rejected.
    private async Task SentinelSetAsync(string containerId, string serviceId, string option, string value, CancellationToken ct)
    {
        var (exitCode, output) = await runtime.ExecWithExitCodeAsync(containerId, ["redis-cli", "-p", "26379", "SENTINEL", "SET", "mymaster", option, value], ct);
        if (exitCode != 0)
        {
            logger.LogWarning("Setting Sentinel {Option} on {ServiceId} failed (exit {ExitCode}): {Output}", option, serviceId, exitCode, output);
            throw new InvalidOperationException($"redis-cli exited {exitCode}: {output}");
        }
    }

    internal readonly record struct RedisRoleObservation(string ServiceId, string Role);

    // Picks the one node reporting ROLE=master among the observations, or null if zero or more than
    // one do. Zero means no node is currently reachable/master-y (probably still starting up); more
    // than one means either a real failover is still mid-flight (the old master hasn't stepped down
    // yet) or a genuine split-brain - in both of those cases guessing which one is "right" and
    // re-pointing Sentinel at it could make things worse, not better, so the caller's only safe move
    // is to do nothing and check again next tick. Extracted as a pure function so this decision rule
    // has a test that doesn't need a live Docker daemon, unlike the rest of this class.
    internal static string? SelectSoleMaster(IEnumerable<RedisRoleObservation> observations)
    {
        var masters = observations.Where(o => o.Role == "master").Select(o => o.ServiceId).ToList();
        return masters.Count == 1 ? masters[0] : null;
    }

    // Sentinel monitors "mymaster" by IP, not by container name - and once its config carries the
    // "# Generated by CONFIG REWRITE" marker, nothing in this stack ever re-resolves that address
    // again for the life of the named volume backing it. If redis-master is ever recreated by
    // something Sentinel itself didn't observe as a failure (a plain `docker compose up`, not a real
    // outage), Docker hands the new container an IP from its reuse pool - Sentinel keeps monitoring
    // the stale one, which a later `up` cycle can just as easily hand to a completely unrelated
    // container. Discovered live in this repo's own dev stack: all three Sentinels ended up agreeing
    // on an IP that belonged to sandbox-aspire-dashboard-1, not Redis at all (see the 3a9af3d review
    // report's F3 for the full incident write-up) - every redirect-api/link-api request needing
    // Redis blocked for a full ConnectTimeout and failed, for as long as that container ran.
    //
    // This periodically re-derives the real master directly - ROLE asked of each of the three actual
    // Redis containers, the same ground-truth check GetRedisTopologyAsync already uses, never
    // trusting Sentinel's own belief about itself - and corrects any Sentinel found monitoring a
    // different address. This never fights a genuine failover: once Sentinel promotes a replica,
    // that replica's own ROLE reports "master" too, so the real master this resolves to is the same
    // one Sentinel already switched to - nothing to correct. It only catches the case Sentinel itself
    // has no way to notice: its remembered address quietly stopped being Redis at all.
    public async Task SelfHealSentinelAsync(CancellationToken ct)
    {
        var observations = new List<RedisRoleObservation>();
        var containersByServiceId = new Dictionary<string, ContainerListResponse>();
        foreach (var serviceId in RedisDataNodes)
        {
            var container = await runtime.FindAsync(serviceId, ct);
            if (container is null)
            {
                continue;
            }

            containersByServiceId[serviceId] = container;
            try
            {
                var output = await runtime.ExecAsync(container.ID, ["redis-cli", "-p", "6379", "ROLE"], ct);
                var role = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                observations.Add(new RedisRoleObservation(serviceId, role switch { "master" => "master", "slave" => "replica", _ => "unreachable" }));
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "SelfHealSentinelAsync: failed to read ROLE from {ServiceId} - skipping this node for this tick", serviceId);
            }
        }

        var realMasterServiceId = SelectSoleMaster(observations);
        if (realMasterServiceId is null)
        {
            return;
        }

        var realMasterIp = await runtime.GetContainerIpAsync(containersByServiceId[realMasterServiceId].ID, ct);
        if (realMasterIp is null)
        {
            return;
        }

        // Best-effort: read whatever down-after/quorum/failover-timeout is currently configured so a
        // correction below can restore it - SENTINEL MONITOR resets a freshly (re-)created "mymaster"
        // entry to sentinel.conf's own template defaults, not necessarily whatever
        // SetSentinelConfigAsync had previously applied through the UI. Falls back to the template's
        // own baked-in values (see sentinel.conf) if even this read fails.
        SentinelConfig? previousConfig;
        try
        {
            previousConfig = await GetSentinelConfigAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "SelfHealSentinelAsync: failed to read current Sentinel config before any correction - falling back to sentinel.conf's own template defaults");
            previousConfig = null;
        }

        var quorum = previousConfig is { Quorum: > 0 } ? previousConfig.Quorum : 2;
        var downAfterMs = previousConfig is { DownAfterMs: > 0 } ? previousConfig.DownAfterMs : 5000;
        var failoverTimeoutMs = previousConfig is { FailoverTimeoutMs: > 0 } ? previousConfig.FailoverTimeoutMs : 10000;

        foreach (var sentinelServiceId in SentinelContainers)
        {
            var sentinelContainer = await runtime.FindAsync(sentinelServiceId, ct);
            if (sentinelContainer is null)
            {
                continue;
            }

            string reportedAddr;
            try
            {
                reportedAddr = await runtime.ExecAsync(sentinelContainer.ID, ["redis-cli", "-p", "26379", "SENTINEL", "get-master-addr-by-name", "mymaster"], ct);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "SelfHealSentinelAsync: failed to query {ServiceId}'s view of mymaster - skipping this tick", sentinelServiceId);
                continue;
            }

            var reportedIp = reportedAddr.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.Equals(reportedIp, realMasterIp, StringComparison.Ordinal))
            {
                continue;
            }

            logger.LogWarning(
                "SelfHealSentinelAsync: {SentinelServiceId} was monitoring {ReportedIp} as mymaster, but {RealMasterServiceId} ({RealMasterIp}) is the only node currently reporting ROLE=master - re-pointing",
                sentinelServiceId, reportedIp, realMasterServiceId, realMasterIp);

            try
            {
                await runtime.ExecAsync(sentinelContainer.ID, ["redis-cli", "-p", "26379", "SENTINEL", "REMOVE", "mymaster"], ct);
                await runtime.ExecAsync(sentinelContainer.ID, ["redis-cli", "-p", "26379", "SENTINEL", "MONITOR", "mymaster", realMasterIp, "6379", quorum.ToString()], ct);
                await SentinelSetAsync(sentinelContainer.ID, sentinelServiceId, "down-after-milliseconds", downAfterMs.ToString(), ct);
                await SentinelSetAsync(sentinelContainer.ID, sentinelServiceId, "failover-timeout", failoverTimeoutMs.ToString(), ct);
                // Not exposed via SentinelConfig/the UI - restored to the template's own value since
                // MONITOR reset it, not because it was ever customizable here.
                await SentinelSetAsync(sentinelContainer.ID, sentinelServiceId, "parallel-syncs", "1", ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "SelfHealSentinelAsync: failed to re-point {ServiceId} at {RealMasterIp} - will retry next tick", sentinelServiceId, realMasterIp);
            }
        }
    }
}
