using System.Text.Json;
using Common;
using Infrastructure.Grpc;
using Microsoft.Extensions.DependencyInjection;
using Polly;

namespace Infrastructure;

// Sync/gRPC mode - calls the downstream service directly and AWAITS the response, which the
// server only sends once the row has actually been persisted (see each worker's own
// MessagingGrpcService, which calls the same batch-consumer handler method with a batch of one).
// Deliberately has no fallback queue underneath it - if the downstream call fails, DispatchAsync
// throws and the caller's request fails too (GlobalExceptionHandler maps RpcException to
// 502/503, and now BrokenCircuitException the same way - see that handler). Silently swallowing
// that failure into a local queue would erase the exact coupling-cost contrast this messaging
// mode exists to demonstrate against the async/bus mode.
//
// The circuit breaker below doesn't change that story, it just makes the failure cheaper once the
// downstream worker is known to be down: without it, every request pays gRPC's real connect/
// deadline cost before failing; with it, a request fails immediately via BrokenCircuitException
// once the breaker's open, same 502/503 outcome either way.
public sealed class SyncGrpcDispatcher(
    MessagingService.MessagingServiceClient client,
    [FromKeyedServices(Constants.GrpcCircuitBreakerKey)] ResiliencePipeline resiliencePipeline) : IEventDispatcher
{
    public async Task DispatchAsync<TMessage>(TMessage message, string topic, CancellationToken cancellationToken = default)
    {
        var request = new PublishRequest
        {
            MessageId = Guid.NewGuid().ToString(),
            Topic = topic,
            Payload = JsonSerializer.Serialize(message),
        };

        await resiliencePipeline.ExecuteAsync(
            ct => new ValueTask<PublishAck>(client.PublishAsync(request, cancellationToken: ct).ResponseAsync),
            cancellationToken);
    }
}
