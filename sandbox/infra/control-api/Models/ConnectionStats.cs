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
