using ControlApi.Models;

namespace ControlApi.Services;

public interface IMongoTopologyService
{
    // Which physical container is actually primary right now, read directly off the replica set's
    // own rs.status() rather than assumed from architecture.json's static labels - MongoDB elects
    // its own primary with zero involvement from this app. See NodeRole.
    Task<InfraTopology> GetMongoTopologyAsync(CancellationToken ct);

    // Drops mongo2/mongo3 from rs0's own voting configuration (rs.reconfig(), run against mongo1) or
    // adds them back (rs.add()) - a plain container stop/start is not enough either direction, unlike
    // Postgres/Redis: rs0's voting members list is independent of which containers happen to be
    // running, so stopping mongo2/mongo3 while they're still configured members would leave mongo1
    // able to see only 1 of 3 votes (not a majority) and step down to SECONDARY, refusing writes -
    // the opposite of "simplify to one working instance". Called by
    // InfraToggleService.SetMongoReplicasEnabledAsync around the container stop/start.
    Task SetReplicasEnabledAsync(bool enabled, CancellationToken ct);
}
