namespace Infrastructure;

// Sits ABOVE ILocalPublishQueue/IMessagePublisher, not a replacement for either - swapping
// IMessagePublisher alone would still return instantly in both messaging modes (the queue already
// absorbs any latency), which would defeat the entire point of a sync-vs-async comparison.
// Controllers depend on this single interface; Program.cs decides which implementation backs it
// based on Messaging:Mode - zero branching in controller/business logic either way.
public interface IEventDispatcher
{
    Task DispatchAsync<TMessage>(TMessage message, string topic, CancellationToken cancellationToken = default);
}
