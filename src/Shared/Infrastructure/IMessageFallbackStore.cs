namespace Infrastructure;

public interface IMessageFallbackStore
{
    Task SaveAsync(FallbackMessage message, CancellationToken cancellationToken);
    Task<IReadOnlyList<FallbackMessage>> GetPendingAsync(CancellationToken cancellationToken);
    Task DeleteAsync(string messageId, CancellationToken cancellationToken);
}
