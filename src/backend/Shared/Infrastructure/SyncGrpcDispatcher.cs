using System.Text.Json;
using Infrastructure.Grpc;

namespace Infrastructure;

// Sync/gRPC mode - calls the downstream service directly and AWAITS the response, which the
// server only sends once the row has actually been persisted (see each worker's own
// MessagingGrpcService, which calls the same batch-consumer handler method with a batch of one).
// Deliberately has no fallback queue underneath it - if the downstream call fails, DispatchAsync
// throws and the caller's request fails too (GlobalExceptionHandler maps RpcException to
// 502/503). Silently swallowing that failure into a local queue would erase the exact coupling-
// cost contrast this messaging mode exists to demonstrate against the async/bus mode.
public sealed class SyncGrpcDispatcher(MessagingService.MessagingServiceClient client) : IEventDispatcher
{
    public async Task DispatchAsync<TMessage>(TMessage message, string topic, CancellationToken cancellationToken = default)
    {
        var request = new PublishRequest
        {
            MessageId = Guid.NewGuid().ToString(),
            Topic = topic,
            Payload = JsonSerializer.Serialize(message),
        };

        await client.PublishAsync(request, cancellationToken: cancellationToken);
    }
}
