using Common;
using Contracts.Events;
using Infrastructure;
using Infrastructure.Grpc;

namespace ShortenerService;

// Server side of the messaging-mode toggle - only reachable at all when a client's
// SyncGrpcDispatcher is actually configured to call this service; always hosted regardless of
// this service's own mode (see GrpcMessagingExtensions.AddMessagingGrpcServer). Calls the exact
// same persistence method the RabbitMQ batch consumer uses, just with a batch of one - see
// LinkCreatedConsumer.HandleBatchAsync's own comment for why that's redelivery-safe either way.
public sealed class MessagingGrpcService(LinkCreatedConsumer consumer) : MessagingGrpcServiceBase<LinkCreatedEvent>(Topics.LinkCreated)
{
    protected override Task HandleAsync(BatchItem<LinkCreatedEvent> item, CancellationToken cancellationToken) =>
        consumer.HandleBatchAsync([item], cancellationToken);
}
