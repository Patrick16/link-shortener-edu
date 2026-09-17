namespace Infrastructure;

public interface IMessageFallbackStore
{
    Task SaveAsync(FallbackMessage message, CancellationToken cancellationToken);
}
