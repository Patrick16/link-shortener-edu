using Common.Models;

namespace Infrastructure;

public interface IClickMetaStore
{
    // Upserts every meta by Id in one round trip - safe to call with a mix of brand-new and
    // already-stored ids (redelivery), since each is an independent upsert.
    Task SaveManyAsync(IReadOnlyCollection<ClickMeta> metas, CancellationToken cancellationToken = default);

    Task<bool> PingAsync(CancellationToken cancellationToken = default);
}
