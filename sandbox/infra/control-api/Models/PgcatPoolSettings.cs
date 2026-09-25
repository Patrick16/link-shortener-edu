namespace ControlApi.Models;

// The pgcat.toml pool-level settings exposed as an experimental control, applied identically to all
// 3 pools (users_db/links_db/clicks_db) rather than per-pool, to keep this to one set of knobs
// instead of nine. Rewritten straight to pgcat.toml (see DockerService.SetPgcatPoolSettingsAsync) -
// pgcat's own `autoreload = 15000` picks the new file up within ~15s on its own, no container
// recreate needed. ReadWriteSplitting maps to query_parser_read_write_splitting - see
// pgcat.toml's own header comment for why that flag (not query_parser_enabled alone) is what
// actually routes reads to replicas.
public record PgcatPoolSettings(string PoolMode, bool ReadWriteSplitting, int PoolSize);
