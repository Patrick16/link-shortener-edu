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

    // Same key LinkExpiryWorker itself claims/renews - see that worker's own LeaderKey constant.
    // Duplicated here rather than shared via a referenced constant: control-api and the product
    // backend are two entirely separate solutions/deployments (see docs/repository-map.md), with no
    // existing shared-constants channel between them, and introducing one just for a single string
    // literal would be a bigger change than the string staying in sync by inspection.
    private const string LinkExpiryLeaderKey = "leader:shortener-service:link-expiry";

    // Found LIVE, not by inspection: an earlier version of this method read straight off the
    // "redis-master" container by name and silently reported "no leader" the moment that container
    // wasn't actually the one LinkExpiryWorker's Sentinel-resolved client was writing to - the same
    // "redis-master is only a label" caveat GetRedisTopologyAsync's own comment already covers,
    // just missed here on the first pass. Re-derives the real master the identical way
    // GetRedisTopologyAsync does (ROLE asked of each of the three data nodes, SelectSoleMaster) and
    // reads the lease off THAT container - including reporting "unknown" rather than guessing
    // during the exact ambiguous window (zero or more than one node self-reporting master) this
    // class's own SelfHealSentinelAsync exists to correct.
    public async Task<LinkExpiryLeaderStatus> GetLinkExpiryLeaderAsync(CancellationToken ct)
    {
        var observations = new List<RedisRoleObservation>();
        var containersByServiceId = new Dictionary<string, ContainerListResponse>();
        foreach (var serviceId in RedisDataNodes)
        {
            var node = await runtime.FindAsync(serviceId, ct);
            if (node is null)
            {
                continue;
            }

            containersByServiceId[serviceId] = node;
            try
            {
                var output = await runtime.ExecAsync(node.ID, ["redis-cli", "-p", "6379", "ROLE"], ct);
                var role = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                observations.Add(new RedisRoleObservation(serviceId, role switch { "master" => "master", "slave" => "replica", _ => "unreachable" }));
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "GetLinkExpiryLeaderAsync: failed to read ROLE from {ServiceId} - skipping this node", serviceId);
            }
        }

        var masterServiceId = SelectSoleMaster(observations);
        if (masterServiceId is null)
        {
            return new LinkExpiryLeaderStatus(null, null);
        }

        var container = containersByServiceId[masterServiceId];

        // redis-cli's non-interactive (non-tty) output is raw - no "(nil)"/"(integer)" decoration,
        // same assumption GetSentinelConfigAsync's own comment already confirmed live for this
        // codebase's docker-exec setup. A free key's GET comes back as an empty line.
        var leaderOutput = await runtime.ExecAsync(container.ID, ["redis-cli", "-p", "6379", "GET", LinkExpiryLeaderKey], ct);
        var leader = leaderOutput.Trim();
        if (leader.Length == 0)
        {
            return new LinkExpiryLeaderStatus(null, null);
        }

        var ttlOutput = await runtime.ExecAsync(container.ID, ["redis-cli", "-p", "6379", "PTTL", LinkExpiryLeaderKey], ct);
        var ttlMs = long.TryParse(ttlOutput.Trim(), out var parsed) && parsed >= 0 ? parsed : (long?)null;
        return new LinkExpiryLeaderStatus(leader, ttlMs);
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
        var realMaster = await FindRealMasterAsync(ct);
        if (realMaster is null)
        {
            return;
        }

        var (realMasterServiceId, realMasterIp) = realMaster.Value;

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

    // Bug found live (2026-10-29 topology QA pass): SelfHealSentinelAsync above only corrects the
    // SENTINELS' own belief about who's master - a replica that already committed to a WRONG master
    // address is never corrected by anything. redis-node-entrypoint.sh's `--replicaof <ip> <port>`
    // uses a literal IP (that's all Sentinel's own protocol ever returns), and Redis never
    // re-resolves a literal IP on its own, so a replica that asked Sentinel before
    // SelfHealSentinelAsync's correction landed (the race InfraToggleService.
    // SetRedisSentinelEnabledInternalAsync's start-Sentinels-before-replicas ordering now minimizes,
    // but doesn't fully eliminate - Docker's StartContainerAsync returns once the start is issued,
    // not once the process inside is actually answering) stays stuck retrying a dead address
    // forever: `connected_slaves:0` on the real master, which write-fences every Redis write once
    // min-replicas-to-write is back to 1. Same ground-truth ROLE read as SelfHealSentinelAsync
    // (FindRealMasterAsync), just corrected on the REPLICA side (REPLICAOF) instead of the
    // Sentinels' side - called right after SelfHealSentinelAsync everywhere that runs (the
    // Sentinel-enable toggle and SentinelSelfHealPollerService's own 30s tick), so this is a real
    // backstop, not just a one-time fix applied at toggle time.
    public async Task SelfHealReplicasAsync(CancellationToken ct)
    {
        var realMaster = await FindRealMasterAsync(ct);
        if (realMaster is null)
        {
            return;
        }

        var (realMasterServiceId, realMasterIp) = realMaster.Value;

        foreach (var serviceId in RedisDataNodes)
        {
            if (serviceId == realMasterServiceId)
            {
                continue;
            }

            var container = await runtime.FindAsync(serviceId, ct);
            if (container is null)
            {
                continue;
            }

            string infoOutput;
            try
            {
                infoOutput = await runtime.ExecAsync(container.ID, ["redis-cli", "-p", "6379", "INFO", "replication"], ct);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "SelfHealReplicasAsync: failed to read replication INFO from {ServiceId} - skipping this tick", serviceId);
                continue;
            }

            var masterHost = infoOutput
                .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(l => l.StartsWith("master_host:", StringComparison.Ordinal))
                ?["master_host:".Length..];

            // Only re-points on a genuine address mismatch, never just because master_link_status is
            // momentarily "down" - a replica correctly pointed at the real master's IP reconnects on
            // its own (a real network blip), and re-issuing REPLICAOF on every such blip would just
            // restart its full resync for no reason.
            if (string.Equals(masterHost, realMasterIp, StringComparison.Ordinal))
            {
                continue;
            }

            logger.LogWarning(
                "SelfHealReplicasAsync: {ServiceId} was replicating from {MasterHost}, but {RealMasterServiceId} ({RealMasterIp}) is the only node currently reporting ROLE=master - re-pointing",
                serviceId, masterHost ?? "(none)", realMasterServiceId, realMasterIp);

            try
            {
                await runtime.ExecAsync(container.ID, ["redis-cli", "-p", "6379", "REPLICAOF", realMasterIp, "6379"], ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "SelfHealReplicasAsync: failed to re-point {ServiceId} at {RealMasterIp} - will retry next tick", serviceId, realMasterIp);
            }
        }
    }

    // Shared by SelfHealSentinelAsync/SelfHealReplicasAsync - ROLE asked of each of the three data
    // nodes directly (never trusting Sentinel's or a replica's own belief about itself), then
    // SelectSoleMaster's same ambiguity rule both callers need: zero or more than one node
    // self-reporting master means "don't guess, check again next tick."
    private async Task<(string ServiceId, string Ip)?> FindRealMasterAsync(CancellationToken ct)
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
                logger.LogDebug(ex, "FindRealMasterAsync: failed to read ROLE from {ServiceId} - skipping this node for this tick", serviceId);
            }
        }

        var realMasterServiceId = SelectSoleMaster(observations);
        if (realMasterServiceId is null)
        {
            return null;
        }

        var realMasterIp = await runtime.GetContainerIpAsync(containersByServiceId[realMasterServiceId].ID, ct);
        return realMasterIp is null ? null : (realMasterServiceId, realMasterIp);
    }
}
