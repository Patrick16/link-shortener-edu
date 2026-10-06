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

        await HandleAsync(new BatchItem<TEvent>(request.MessageId, message), context.CancellationToken);

        return new PublishAck { Success = true };
    }

    protected abstract Task HandleAsync(BatchItem<TEvent> item, CancellationToken cancellationToken);
}
