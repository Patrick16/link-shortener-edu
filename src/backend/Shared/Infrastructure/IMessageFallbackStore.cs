namespace Infrastructure;

public interface IMessageFallbackStore
{
    Task SaveAsync(FallbackMessage message, CancellationToken cancellationToken);
    Task<IReadOnlyList<FallbackMessage>> GetPendingAsync(CancellationToken cancellationToken);
    Task DeleteAsync(string messageId, CancellationToken cancellationToken);

    // Clears ClaimedAt on a row whose republish attempt just failed, so it's eligible again on the
    // next poll tick instead of sitting claimed until StaleClaimThreshold elapses - found in review:
    // without this, a failed retry effectively backs off ~2 minutes (the stale-claim window), not
    // the ~15s NextPollDelay's own comment promises, since GetPendingAsync only re-offers a row once
    // its claim goes stale.
    Task ReleaseClaimAsync(string messageId, CancellationToken cancellationToken);
}
