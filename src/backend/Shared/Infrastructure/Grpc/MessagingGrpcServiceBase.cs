using System.Text.Json;
using Grpc.Core;
using Infrastructure;

namespace Infrastructure.Grpc;

// Shared server-side shape for the messaging-mode toggle's gRPC endpoint. Topic-keyed rather than
// generic-over-one-TEvent: a single concrete MessagingGrpcService per host maps to the gRPC
// service's one fixed route (ASP.NET Core can't map two classes to the same
// MessagingService.MessagingServiceBase route), but a host can need to handle more than one event
// type through it - ShortenerService does, once LinkDeletedEvent also respects Messaging:Mode
// alongside LinkCreatedEvent. Concrete classes build their topic->handler map with ForTopic below
// and pass it to this base constructor; TrafficService's ClickTrackedEvent-only case is just a
// map with one entry.
public abstract class MessagingGrpcServiceBase(
    IReadOnlyDictionary<string, MessagingGrpcServiceBase.TopicHandler> handlersByTopic) : MessagingService.MessagingServiceBase
{
    protected delegate Task<BatchOutcome> TopicHandler(string messageId, string payloadJson, CancellationToken cancellationToken);

    public override async Task<PublishAck> Publish(PublishRequest request, ServerCallContext context)
    {
        if (!handlersByTopic.TryGetValue(request.Topic, out var handler))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"Unknown topic: {request.Topic}"));
        }

        var outcome = await handler(request.MessageId, request.Payload, context.CancellationToken);

        // Unlike the RabbitMQ transport (which dead-letters a poison message and acks the rest of
        // its batch), a sync caller is waiting for exactly this one message's outcome - a poison
        // result here means the business rule it violated (e.g. a unique-key conflict) is
        // permanent, so it's surfaced as AlreadyExists rather than a false PublishAck{Success=true}.
        var poison = outcome.PoisonMessages.FirstOrDefault(p => p.MessageId == request.MessageId);
        if (poison is not null)
        {
            throw new RpcException(new Status(StatusCode.AlreadyExists, poison.Reason));
        }

        return new PublishAck { Success = true };
    }

    // Builds one topic's handler: deserialize-payload-as-TEvent, then dispatch as a batch of one to
    // the given consumer method. Kept here, generic, so concrete classes (one per host) don't each
    // re-implement the deserialize/error-wrap boilerplate per event type they support.
    protected static TopicHandler ForTopic<TEvent>(Func<BatchItem<TEvent>, CancellationToken, Task<BatchOutcome>> handleAsync) =>
        (messageId, payloadJson, cancellationToken) =>
        {
            var message = JsonSerializer.Deserialize<TEvent>(payloadJson)
                ?? throw new RpcException(new Status(StatusCode.InvalidArgument, $"Payload did not deserialize to a {typeof(TEvent).Name}."));
            return handleAsync(new BatchItem<TEvent>(messageId, message), cancellationToken);
        };
}
