using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Infrastructure;

// Only registered in messaging-mode=grpc (see HealthChecksBuilderExtensions.AddGrpcMessagingHealthCheck),
// against the same GrpcChannel singleton SyncGrpcDispatcher calls on every POST /Links or click
// (see GrpcMessagingExtensions.AddEventDispatcher). Without this, /health/ready only ever checked
// Postgres/Redis and stayed Healthy even when the downstream gRPC server (ShortenerService/
// TrafficService) was down - every request still failed with 502/503 from GlobalExceptionHandler,
// but Docker/nginx kept routing live traffic to a replica that could only ever fail (found during
// review).
//
// Deliberately does NOT call the real Publish RPC - the contract's only method - since that would
// actually persist a fake LinkCreatedEvent/ClickTrackedEvent downstream on every health probe.
// GrpcChannel.State/ConnectAsync only inspect/(re)establish the underlying HTTP/2 connection, with
// no application-level side effect.
public sealed class GrpcChannelHealthCheck(GrpcChannel channel) : IHealthCheck
{
    // Same budget as RabbitMqHealthCheck: bounded well under docker-compose's healthcheck
    // `timeout: 5s` so a stuck connection fails the probe fast instead of hanging past Docker's own
    // timeout anyway.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (channel.State == ConnectivityState.Ready)
        {
            return HealthCheckResult.Healthy();
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(Timeout);

        try
        {
            // ConnectAsync (re)establishes the channel's HTTP/2 connection and completes once it
            // reaches Ready - the built-in equivalent of manually looping on WaitForStateChangedAsync
            // through Idle/Connecting/TransientFailure.
            await channel.ConnectAsync(cts.Token);
            return HealthCheckResult.Healthy();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy(
                $"gRPC channel to {channel.Target} did not become ready within {Timeout.TotalSeconds}s (state: {channel.State}).");
        }
    }
}
