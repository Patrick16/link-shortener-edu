namespace Infrastructure;

// Default/async mode - wraps today's ILocalPublishQueue.EnqueueAsync exactly as controllers used
// to call it directly. Returns once enqueued; the real RabbitMQ publish (and its SQLite fallback +
// RabbitMqRetryWorker) happens off this request's thread, completely unchanged by this toggle.
public sealed class AsyncQueueDispatcher(ILocalPublishQueue publishQueue) : IEventDispatcher
{
    public Task DispatchAsync<TMessage>(TMessage message, string topic, CancellationToken cancellationToken = default) =>
        publishQueue.EnqueueAsync(message, topic, cancellationToken).AsTask();
}
