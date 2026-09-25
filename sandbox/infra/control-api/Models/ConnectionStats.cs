namespace ControlApi.Models;

// One pgcat pool's connection counts (`SHOW POOLS`) - client side (app -> pgcat) and server side
// (pgcat -> postgres) are deliberately both surfaced: the gap between them (many clients, few
// servers) is the whole point of a connection pooler, and is invisible if you only look at one side.
public record PoolConnectionStats(string Database, int ClientIdle, int ClientActive, int ClientWaiting, int ServerActive, int ServerIdle, int ServerUsed);

public record PgcatConnectionStats(IReadOnlyList<PoolConnectionStats> Pools);

// Real backend connections on Postgres itself (`pg_stat_activity`), grouped by database - directly
// comparable to PgcatConnectionStats' server-side counts to confirm pooling is actually happening
// (e.g. many pgcat clients but few real postgres backends).
public record PostgresConnectionStats(IReadOnlyDictionary<string, int> ConnectionsByDatabase, int Total);

// Which physical container is actually playing which role right now, for a cluster that can
// re-elect its own leader without this app doing anything (Redis Sentinel failover, MongoDB
// replica-set election) - unlike Postgres in this stack, which has streaming replication but no
// automatic promotion tool, so "postgres" is always the primary until someone manually intervenes
// outside the app entirely. architecture.json's node ids/labels are static and can't reflect this;
// the graph asks for this live instead of assuming a fixed role from a node's own name.
// Role is "master"/"replica"/"unreachable" for Redis, "primary"/"secondary"/"unreachable" for Mongo.
public record NodeRole(string ServiceId, string Role);

public record InfraTopology(IReadOnlyList<NodeRole> Roles);
