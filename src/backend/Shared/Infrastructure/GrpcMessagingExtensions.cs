using Common;
using Grpc.Net.Client;
using Infrastructure.Grpc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Infrastructure;

// DI wiring for both sides of the messaging-mode toggle.
public static class GrpcMessagingExtensions
{
    private const string GrpcMode = "grpc";
    private const string RabbitMqMode = "rabbitmq";

    // Single source of truth for parsing Messaging:Mode - used by AddEventDispatcher below and by
    // Program.cs in LinkApi/RedirectApi to decide whether AddRabbitMqPublisher is even needed.
    // Unlike the first version (found during review), an unrecognized value now fails loudly at
    // startup instead of silently behaving as "rabbitmq" - a typo'd/misconfigured env var used to
    // produce no warning anywhere, just gRPC mode quietly never activating.
    public static bool IsMessagingGrpcMode(this IConfiguration configuration)
    {
        var mode = configuration[Constants.MessagingModeSection];
        if (string.IsNullOrEmpty(mode) || string.Equals(mode, RabbitMqMode, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(mode, GrpcMode, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        throw new InvalidOperationException(
            $"Unrecognized {Constants.MessagingModeSection} value '{mode}' - expected '{RabbitMqMode}' or '{GrpcMode}'.");
    }

    // Client side (LinkApi, RedirectApi) - the controller's one IEventDispatcher dependency is
    // either AsyncQueueDispatcher (default) or SyncGrpcDispatcher, selected once at startup by
    // Messaging:Mode. Nothing downstream of this call branches on the mode again.
    public static IHostApplicationBuilder AddEventDispatcher(this IHostApplicationBuilder builder)
    {
        if (builder.Configuration.IsMessagingGrpcMode())
        {
            var targetUrl = builder.Configuration[Constants.MessagingGrpcTargetUrlSection]
                ?? throw new InvalidOperationException(
                    $"{Constants.MessagingGrpcTargetUrlSection} must be set when {Constants.MessagingModeSection}={GrpcMode}.");

            // Required to call a gRPC server over plaintext HTTP/2 (h2c), which is what every
            // Messaging:Grpc:TargetUrl in this stack is (http://nginx:..., no TLS) - without this
            // switch, SocketsHttpHandler refuses to negotiate HTTP/2 in cleartext, silently
            // downgrades to HTTP/1.1, and Grpc.Net.Client rejects that downgrade with "Bad gRPC
            // response. Response protocol downgraded to HTTP/1.1." on the very first call (found
            // during review, not guessed - confirmed nowhere else in the repo sets this switch).
            AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

            builder.Services.AddSingleton(_ => GrpcChannel.ForAddress(targetUrl));
            builder.Services.AddSingleton(sp => new MessagingService.MessagingServiceClient(sp.GetRequiredService<GrpcChannel>()));
            builder.Services.AddSingleton<IEventDispatcher, SyncGrpcDispatcher>();
        }
        else
        {
            builder.Services.AddSingleton<IEventDispatcher, AsyncQueueDispatcher>();
        }

        return builder;
    }

    // Server side (ShortenerService, TrafficService only - not ReportingService, which has no
    // caller for one) - always hosts the gRPC endpoint regardless of the current mode; it's cheap
    // to leave listening, and which transport actually gets used is entirely decided by the
    // CLIENT's dispatcher above. Each service still has to map its own MessagingGrpcService after
    // Build() (topic routing differs per service).
    public static IHostApplicationBuilder AddMessagingGrpcServer(this IHostApplicationBuilder builder)
    {
        builder.Services.AddGrpc();
        return builder;
    }
}
