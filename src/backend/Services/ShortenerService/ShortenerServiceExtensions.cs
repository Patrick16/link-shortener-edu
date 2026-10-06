using Microsoft.Extensions.DependencyInjection;

namespace ShortenerService;

// ShortenerService-only wiring - the parts of Program.cs that no other service shares.
public static class ShortenerServiceExtensions
{
    public static WebApplicationBuilder AddLinkEventConsumers(this WebApplicationBuilder builder)
    {
        // Registered as itself first, then wrapped as the IHostedService - not just
        // AddHostedService<LinkCreatedConsumer>() - because MessagingGrpcService (gRPC mode's
        // server side) also needs to resolve LinkCreatedConsumer directly, to call its
        // HandleBatchAsync with a batch of one. AddHostedService<T> alone only registers T as
        // IHostedService, not as T itself.
        builder.Services.AddSingleton<LinkCreatedConsumer>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<LinkCreatedConsumer>());
        builder.Services.AddHostedService<ClickTrackedConsumer>();
        return builder;
    }
}
