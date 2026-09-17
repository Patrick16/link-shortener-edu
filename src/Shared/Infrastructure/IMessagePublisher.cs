namespace Infrastructure;

public interface IMessagePublisher
{
    Task PublishAsync<TMessage>(TMessage message, string topic, CancellationToken cancellationToken);

    // Retries a previously failed message as-is (same MessageId). Returns whether the retry succeeded;
    // does not touch the fallback store either way — the caller decides what to do with that.
    Task<bool> TryRepublishAsync(FallbackMessage message, CancellationToken cancellationToken);
}
