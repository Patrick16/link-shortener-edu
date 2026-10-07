using ControlApi.Models;

namespace ControlApi.Services;

public interface IMongoTopologyService
{
    // Which physical container is actually primary right now, read directly off the replica set's
    // own rs.status() rather than assumed from architecture.json's static labels - MongoDB elects
    // its own primary with zero involvement from this app. See NodeRole.
    Task<InfraTopology> GetMongoTopologyAsync(CancellationToken ct);
}
