using Common;
using Contracts.Events;
using Infrastructure;
using Infrastructure.Grpc;

namespace ShortenerService;

// Server side of the messaging-mode toggle - only reachable at all when a client's
// SyncGrpcDispatcher is actually configured to call this service; always hosted regardless of
// this service's own mode (see GrpcMessagingExtensions.AddMessagingGrpcServer). Handles both
// LinkCreatedEvent and LinkDeletedEvent - unlike TrafficService's single-topic MessagingGrpcService,
// this one needs two, since LinkDeletedEvent also respects Messaging:Mode (same toggle as create,
// not a forced transport like custom aliases) and ASP.NET Core only allows one class to be mapped
// per gRPC service route - see MessagingGrpcServiceBase's own comment. Calls the exact same
// persistence method each RabbitMQ batch consumer uses, just with a batch of one - see each
// consumer's HandleBatchAsync for why that's redelivery-safe either way.
public sealed class MessagingGrpcService(LinkCreatedConsumer createdConsumer, LinkDeletedConsumer deletedConsumer)
    : MessagingGrpcServiceBase(new Dictionary<string, TopicHandler>
    {
        [Topics.LinkCreated] = ForTopic<LinkCreatedEvent>((item, ct) => createdConsumer.HandleBatchAsync([item], ct)),
        [Topics.LinkDeleted] = ForTopic<LinkDeletedEvent>((item, ct) => deletedConsumer.HandleBatchAsync([item], ct)),
    })
{
}
