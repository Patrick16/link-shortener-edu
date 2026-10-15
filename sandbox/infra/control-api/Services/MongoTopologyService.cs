using System.Text.Json;
using ControlApi.Models;

namespace ControlApi.Services;

public sealed class MongoTopologyService(IContainerRuntime runtime, ILogger<MongoTopologyService> logger) : IMongoTopologyService
{
    // The three-node Mongo replica set whose primary is elected among themselves, same "can change
    // with no app involvement" story as Redis Sentinel.
    private static readonly IReadOnlyList<string> MongoNodes = ["mongo1", "mongo2", "mongo3"];

    // MongoDB elects its own primary among the replica set members with no external tool involved -
    // the same "the graph's static node labels can lie" problem as Redis Sentinel. rs.status() has
    // to run against a member that's actually reachable, so this tries mongo1/2/3 in turn and uses
    // whichever first responds; its view covers every member (reachable or not) in one call, so only
    // one successful exec is needed per poll instead of one per node.
    public async Task<InfraTopology> GetMongoTopologyAsync(CancellationToken ct)
    {
        foreach (var serviceId in MongoNodes)
        {
            var container = await runtime.FindAsync(serviceId, ct);
            if (container is null)
            {
                continue;
            }

            try
            {
                var output = await runtime.ExecAsync(container.ID,
                    ["mongosh", "--quiet", "--eval", "JSON.stringify(rs.status().members.map(m => ({ name: m.name, state: m.stateStr })))"], ct);

                // mongosh can print a version/connection banner before the eval result even with
                // --quiet - slicing from the first '[' skips over that instead of assuming the whole
                // output is clean JSON.
                var start = output.IndexOf('[');
                if (start < 0)
                {
                    continue;
                }

                using var doc = JsonDocument.Parse(output[start..]);
                var roles = new List<NodeRole>();
                foreach (var member in doc.RootElement.EnumerateArray())
                {
                    var name = member.GetProperty("name").GetString() ?? "";
                    var memberServiceId = name.Split(':')[0];
                    var state = member.GetProperty("state").GetString() ?? "";
                    roles.Add(new NodeRole(memberServiceId, state switch
                    {
                        "PRIMARY" => "primary",
                        "SECONDARY" => "secondary",
                        _ => "unreachable",
                    }));
                }

                return new InfraTopology(roles);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to read Mongo replica set status from {ServiceId}", serviceId);
            }
        }

        // Every member unreachable (or none responded) - report all three as unreachable rather than
        // an empty list, so the UI still has something to render.
        return new InfraTopology(MongoNodes.Select(id => new NodeRole(id, "unreachable")).ToList());
    }

    // rs.reconfig()/rs.add() only succeed when run against the CURRENT PRIMARY - confirmed live:
    // MongoDB elects a primary among whichever members exist at rs.initiate() time with no regard
    // for container naming, so mongo1 is not reliably it (a fresh stack came up with mongo3 as
    // primary on a real run of this code). Finding the real primary first, rather than assuming
    // mongo1, is what makes rs.add() (the ON path) work regardless of which member the replica set
    // happens to have elected. The OFF path needs more than just this, though - see
    // EnsureMongo1IsPrimaryAsync's own comment.
    private async Task<string?> TryFindPrimaryServiceIdAsync(CancellationToken ct)
    {
        foreach (var serviceId in MongoNodes)
        {
            var container = await runtime.FindAsync(serviceId, ct);
            if (container is null)
            {
                continue;
            }

            try
            {
                // Always wrapped in an object (never a bare value or "null") specifically so the
                // result has one unambiguous start character to slice from - same banner-skipping
                // problem GetMongoTopologyAsync's own comment describes, but a bare "null" would
                // give no '{'/'[' to search for at all.
                var output = await runtime.ExecAsync(container.ID,
                    ["mongosh", "--quiet", "--eval", "JSON.stringify({ primaryName: (rs.status().members.find(m => m.stateStr === 'PRIMARY') || {}).name })"], ct);

                var start = output.IndexOf('{');
                if (start < 0)
                {
                    continue;
                }

                using var doc = JsonDocument.Parse(output[start..]);
                if (doc.RootElement.GetProperty("primaryName").GetString() is not { } name)
                {
                    continue;
                }

                return name.Split(':')[0];
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "TryFindPrimaryServiceIdAsync: {ServiceId} couldn't report rs0's primary - trying the next node", serviceId);
            }
        }

        return null;
    }

    private async Task<Docker.DotNet.Models.ContainerListResponse> FindPrimaryContainerAsync(CancellationToken ct)
    {
        var serviceId = await TryFindPrimaryServiceIdAsync(ct)
            ?? throw new InvalidOperationException("Could not find rs0's current primary - no Mongo node answered rs.status()");
        return await runtime.FindAsync(serviceId, ct)
            ?? throw new InvalidOperationException($"{serviceId} container not found, despite just reporting itself as rs0's primary");
    }

    // MongoDB refuses to let a node reconfigure ITSELF out of rs0's member list - confirmed live
    // (including with {force: true}, which does not help here): "No host described in new
    // configuration ... maps to this node". Unlike Postgres/Redis, there is no way to remove the
    // current primary's own membership from its own connection - the removal has to be run from a
    // DIFFERENT member's connection, one that survives the new config. Since mongo1 is this toggle's
    // one permanent member, every removal in SetReplicasEnabledAsync's OFF path runs from mongo1's
    // own connection - which only works if mongo1 is already primary. If it isn't (MongoDB's own
    // election after rs.initiate() has no reason to favor any particular member), this temporarily
    // raises mongo1's priority above the other two (a priority-only reconfig, not a voting-member
    // count change, so it isn't subject to the "1 at a time" limit above) and steps the current
    // primary down, then polls until mongo1 reports itself primary. A no-op, with nothing touched,
    // when mongo1 already is.
    private async Task EnsureMongo1IsPrimaryAsync(CancellationToken ct)
    {
        var primaryServiceId = await TryFindPrimaryServiceIdAsync(ct);
        if (primaryServiceId == "mongo1")
        {
            return;
        }

        if (primaryServiceId is not null)
        {
            var currentPrimary = await runtime.FindAsync(primaryServiceId, ct)
                ?? throw new InvalidOperationException($"{primaryServiceId} container not found, despite just reporting itself as rs0's primary");

            const string stepDownScript = """
                var cfg = rs.conf();
                cfg.members.forEach(m => { m.priority = (m.host === "mongo1:27017") ? 2 : 1; });
                cfg.version += 1;
                rs.reconfig(cfg);
                rs.stepDown();
                """;
            try
            {
                await runtime.ExecAsync(currentPrimary.ID, ["mongosh", "--quiet", "--eval", stepDownScript], ct);
            }
            catch (Exception ex)
            {
                // stepDown legitimately disrupts its own command's response once the node actually
                // demotes mid-call - the polling loop below is the real confirmation, not this.
                logger.LogDebug(ex, "EnsureMongo1IsPrimaryAsync: the priority-bump+stepDown command on {ServiceId} errored (expected if its own connection dropped mid-step-down) - polling for mongo1 to take over regardless", primaryServiceId);
            }
        }

        for (var attempt = 1; attempt <= 15; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            if (await TryFindPrimaryServiceIdAsync(ct) == "mongo1")
            {
                return;
            }
        }

        // The success path's own removeScript resets priorities back to 1 once it's done - this
        // timeout means that script never runs, so without an explicit reset here the priority bump
        // above would be left permanently skewing every future election, not just this one attempt.
        await TryResetMemberPrioritiesAsync(ct);
        throw new InvalidOperationException("mongo1 did not become rs0's primary after stepping the previous one down - cannot safely remove mongo2/mongo3 without risking a self-removal error on whichever node is still primary");
    }

    // Best-effort cleanup for EnsureMongo1IsPrimaryAsync's timeout path - see its own comment. Finds
    // whichever node is CURRENTLY primary (not necessarily mongo1 - the election this is cleaning up
    // after never confirmed settling on it) and resets every member's priority back to the shipped
    // default (1) from there.
    private async Task TryResetMemberPrioritiesAsync(CancellationToken ct)
    {
        var primaryServiceId = await TryFindPrimaryServiceIdAsync(ct);
        var container = primaryServiceId is null ? null : await runtime.FindAsync(primaryServiceId, ct);
        if (container is null)
        {
            logger.LogWarning("TryResetMemberPrioritiesAsync: no reachable primary to reset rs0's member priorities from - they may stay skewed until a future call fixes this");
            return;
        }

        const string resetScript = """
            var cfg = rs.conf();
            if (cfg.members.some(m => m.priority !== 1)) {
              cfg.members.forEach(m => { m.priority = 1; });
              cfg.version += 1;
              rs.reconfig(cfg);
            }
            """;
        try
        {
            await runtime.ExecAsync(container.ID, ["mongosh", "--quiet", "--eval", resetScript], ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "TryResetMemberPrioritiesAsync: failed to reset rs0's member priorities from {ServiceId} - they may stay skewed until a future call fixes this", primaryServiceId);
        }
    }

    // Idempotent both directions - checks rs.conf()'s current members first and only
    // reconfigures/adds what's actually missing, so calling this twice in a row (or once per cycle,
    // as InfraToggleService does) never errors on "already a member" or wastefully bumps rs0's
    // config version for no real change.
    public async Task SetReplicasEnabledAsync(bool enabled, CancellationToken ct)
    {
        if (enabled)
        {
            var container = await FindPrimaryContainerAsync(ct);
            const string addScript = """
                var hosts = rs.conf().members.map(m => m.host);
                var added = [];
                if (hosts.indexOf("mongo2:27017") < 0) { rs.add("mongo2:27017"); added.push("mongo2:27017"); }
                if (hosts.indexOf("mongo3:27017") < 0) { rs.add("mongo3:27017"); added.push("mongo3:27017"); }
                print(added.length > 0 ? ("added: " + added.join(", ")) : "already 3 members");
                """;
            await RunMongoshOrThrowAsync(container.ID, addScript, enabled, ct);
            return;
        }

        // Must run from mongo1's own connection - see EnsureMongo1IsPrimaryAsync's own comment.
        await EnsureMongo1IsPrimaryAsync(ct);
        var mongo1 = await runtime.FindAsync("mongo1", ct)
            ?? throw new InvalidOperationException("mongo1 container not found");

        // A non-forced rs.reconfig() can also only add or remove at most 1 voting member per call
        // (confirmed live: MongoServerError on a straight 2-member removal) - so mongo2/mongo3 come
        // out one reconfig at a time, re-reading rs.conf() fresh before each one rather than
        // computing both removals against a single stale snapshot. Resets mongo1's own priority back
        // to the shipped default (1) at the end - a no-op if EnsureMongo1IsPrimaryAsync never had to
        // touch it, but otherwise undoes its temporary bump now that it's no longer needed.
        const string removeScript = """
            var removed = [];
            var toRemove = rs.conf().members.map(m => m.host).filter(h => h !== "mongo1:27017");
            for (var i = 0; i < toRemove.length; i++) {
              var cfg = rs.conf();
              cfg.members = cfg.members.filter(m => m.host !== toRemove[i]);
              cfg.version += 1;
              rs.reconfig(cfg);
              removed.push(toRemove[i]);
            }
            var selfCfg = rs.conf();
            if (selfCfg.members[0].priority !== 1) {
              selfCfg.members[0].priority = 1;
              selfCfg.version += 1;
              rs.reconfig(selfCfg);
            }
            print(removed.length > 0 ? ("removed: " + removed.join(", ")) : "already 1 member");
            """;
        await RunMongoshOrThrowAsync(mongo1.ID, removeScript, enabled, ct);
    }

    private async Task RunMongoshOrThrowAsync(string containerId, string script, bool enabled, CancellationToken ct)
    {
        var (exitCode, output) = await runtime.ExecWithExitCodeAsync(containerId, ["mongosh", "--quiet", "--eval", script], ct);
        if (exitCode != 0)
        {
            logger.LogWarning("Setting Mongo replicas enabled={Enabled} failed (exit {ExitCode}): {Output}", enabled, exitCode, output);
            throw new InvalidOperationException($"mongosh exited {exitCode}: {output}");
        }

        logger.LogWarning("Set Mongo replicas enabled={Enabled}: {Output}", enabled, output.Trim());
    }
}
