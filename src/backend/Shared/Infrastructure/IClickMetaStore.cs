using Common.Models;

namespace Infrastructure;

public interface IClickMetaStore
{
    Task SaveAsync(ClickMeta meta, CancellationToken cancellationToken = default);

    Task<bool> PingAsync(CancellationToken cancellationToken = default);
}
