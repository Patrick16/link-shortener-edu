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
}
