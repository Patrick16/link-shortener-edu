using System.Net;
using System.Net.Sockets;
using Grpc.Net.Client;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Infrastructure.Tests;

public class GrpcChannelHealthCheckTests
{
    // Required for plaintext HTTP/2 (h2c), same switch GrpcMessagingExtensions.AddEventDispatcher
    // sets in the real app - without it SocketsHttpHandler refuses to even attempt HTTP/2 against a
    // plain http:// target.
    static GrpcChannelHealthCheckTests() =>
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

    // Regression test for the bug this check exists to fix: /health/ready used to stay Healthy
    // (Postgres-only) even when the downstream gRPC server (ShortenerService/TrafficService) that
    // SyncGrpcDispatcher calls synchronously was completely unreachable - every request still
    // failed with 502/503, but Docker/nginx never saw it. Binding and immediately releasing a port
    // guarantees nothing is listening on it, i.e. a real "connection refused", not a mock.
    [Fact]
    public async Task CheckHealthAsync_TargetUnreachable_ReturnsUnhealthy()
    {
        var unusedPort = GetUnusedPort();
        using var channel = GrpcChannel.ForAddress($"http://127.0.0.1:{unusedPort}");
        var sut = new GrpcChannelHealthCheck(channel);

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    private static int GetUnusedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
