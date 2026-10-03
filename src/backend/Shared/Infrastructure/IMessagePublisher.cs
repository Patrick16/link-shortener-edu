using System.Diagnostics;

namespace Infrastructure;

public interface IMessagePublisher
{
    // parentContext overrides the ambient Activity.Current as this publish's trace parent - needed
    // when the caller enqueued the publish onto LocalPublishQueue and is now running on a worker's
    // async flow, where Activity.Current is no longer the original HTTP request's (and can't be set
    // back to it - Activity.Current rejects an Activity that has already been Stop()'d, which the
    // request's always has been by the time a worker gets here). Defaults to null, which falls back
    // to the normal ambient-Activity.Current behavior for every other (non-queued) caller.
    Task PublishAsync<TMessage>(TMessage message, string topic, CancellationToken cancellationToken, ActivityContext? parentContext = null);

    // Retries a previously failed message as-is (same MessageId). Returns whether the retry succeeded;
    // does not touch the fallback store either way — the caller decides what to do with that.
    Task<bool> TryRepublishAsync(FallbackMessage message, CancellationToken cancellationToken);
}
