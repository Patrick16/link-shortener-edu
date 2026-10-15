using System.Text.Json;
using Contracts.Events;
using Grpc.Core;
using Infrastructure.Grpc;

namespace Infrastructure;

public enum AliasCreationResult
{
    Created,
    AliasTaken,
}

// Abstracts the synchronous alias-creation RPC the same way IEventDispatcher abstracts the
// standing messaging-mode toggle - LinksController doesn't build PublishRequest/handle RpcException
// itself.
public interface ICustomAliasCreator
{
    Task<AliasCreationResult> CreateAsync(LinkCreatedEvent linkCreatedEvent, CancellationToken cancellationToken);
}

// Always calls ShortenerService's gRPC endpoint directly (see GrpcMessagingExtensions.
// AddAliasGrpcClient) - independent of Messaging:Mode, unlike SyncGrpcDispatcher. Only
// AlreadyExists is caught here and turned into a typed result the controller can branch on; every
// other RpcException (Unavailable, DeadlineExceeded, ...) is a real transport failure and is left
// to propagate to GlobalExceptionHandler's existing 502/503 mapping, the same as SyncGrpcDispatcher.
public sealed class GrpcAliasCreator(MessagingService.MessagingServiceClient client) : ICustomAliasCreator
{
    public async Task<AliasCreationResult> CreateAsync(LinkCreatedEvent linkCreatedEvent, CancellationToken cancellationToken)
    {
        var request = new PublishRequest
        {
            MessageId = Guid.NewGuid().ToString(),
            Topic = Topics.LinkCreated,
            Payload = JsonSerializer.Serialize(linkCreatedEvent),
        };

        try
        {
            await client.PublishAsync(request, cancellationToken: cancellationToken);
            return AliasCreationResult.Created;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.AlreadyExists)
        {
            return AliasCreationResult.AliasTaken;
        }
    }
}
