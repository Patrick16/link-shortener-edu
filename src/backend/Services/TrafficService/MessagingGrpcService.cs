using Common;
using Contracts.Events;
using Infrastructure;
using Infrastructure.Grpc;

namespace TrafficService;

// Server side of the messaging-mode toggle - see ShortenerService's identical class/comment.
// Calls the same batch-consumer handler with a batch of one.
public sealed class MessagingGrpcService(ClickTrackedConsumer consumer) : MessagingGrpcServiceBase<ClickTrackedEvent>(Topics.ClickTracked)
{
    protected override Task HandleAsync(BatchItem<ClickTrackedEvent> item, CancellationToken cancellationToken) =>
        consumer.HandleBatchAsync([item], cancellationToken);
}
