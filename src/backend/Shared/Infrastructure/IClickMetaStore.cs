using Common.Models;

namespace Infrastructure;

public interface IClickMetaStore
{
    // Upserts every meta by Id in one round trip - safe to call with a mix of brand-new and
    // already-stored ids (redelivery), since each is an independent upsert.
    Task SaveManyAsync(IReadOnlyCollection<ClickMeta> metas, CancellationToken cancellationToken = default);

    Task<bool> PingAsync(CancellationToken cancellationToken = default);

    // Creates the TTL index if it doesn't already exist - call once at startup (see
    // MigratePostgresAsync for the equivalent Postgres-side convention). Idempotent: Mongo's
    // CreateOneAsync no-ops if an identical index already exists.
    Task EnsureIndexesAsync(CancellationToken cancellationToken = default);
}
