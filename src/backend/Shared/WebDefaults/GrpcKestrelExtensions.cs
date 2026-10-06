using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace WebDefaults;

public static class GrpcKestrelExtensions
{
    // Dedicated port for gRPC (HTTP/2 cleartext), separate from the existing 8080 health-check
    // listener (HTTP/1.1 only) - rather than configuring Kestrel to multiplex both protocols on
    // one port (possible, but fiddlier to get right with no TLS in this dev stack), each worker
    // just listens twice. Internal-only - never published to the host, only reachable from other
    // containers on the same compose network (nginx's grpc_pass upstreams point here directly).
    public const int GrpcPort = 8090;

    public static WebApplicationBuilder AddGrpcKestrelEndpoint(this WebApplicationBuilder builder)
    {
        // Explicit ListenAnyIP calls in ConfigureKestrel REPLACE whatever ASPNETCORE_HTTP_PORTS/
        // ASPNETCORE_URLS would otherwise bind, not add to it (confirmed live, not guessed - the
        // first version of this only added GrpcPort and the 8080 health-check listener silently
        // vanished, logged as "Overriding address(es) 'http://*:8080'. Binding to endpoints
        // defined via IConfiguration and/or UseKestrel() instead", which then made every
        // healthcheck hang/fail). Both ports have to be listed explicitly here.
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenAnyIP(8080);
            options.ListenAnyIP(GrpcPort, listenOptions => listenOptions.Protocols = HttpProtocols.Http2);
        });
        return builder;
    }
}
