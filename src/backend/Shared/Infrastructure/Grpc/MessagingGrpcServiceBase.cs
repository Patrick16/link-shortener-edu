using System.Text.Json;
using Grpc.Core;
using Infrastructure;

namespace Infrastructure.Grpc;

// Shared server-side shape for the messaging-mode toggle's gRPC endpoint. ShortenerService and
// TrafficService each have their own MessagingGrpcService validating a different topic/event
// type, but the validate-topic / deserialize-payload / dispatch-batch-of-one / ack sequence
// around that was identical in both - factored out here after the two copies drifted to needing
// the same "see X's identical class" comment instead of a shared base (found during review).
public abstract class MessagingGrpcServiceBase<TEvent>(string expectedTopic) : MessagingService.MessagingServiceBase
{
    public override async Task<PublishAck> Publish(PublishRequest request, ServerCallContext context)
    {
        if (request.Topic != expectedTopic)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"Unknown topic: {request.Topic}"));
        }

        var message = JsonSerializer.Deserialize<TEvent>(request.Payload)
            ?? throw new RpcException(new Status(StatusCode.InvalidArgument, $"Payload did not deserialize to a {typeof(TEvent).Name}."));

        var outcome = await HandleAsync(new BatchItem<TEvent>(request.MessageId, message), context.CancellationToken);

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

    protected abstract Task<BatchOutcome> HandleAsync(BatchItem<TEvent> item, CancellationToken cancellationToken);
}
