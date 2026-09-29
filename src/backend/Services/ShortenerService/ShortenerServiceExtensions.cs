namespace ShortenerService;

// ShortenerService-only wiring - the parts of Program.cs that no other service shares.
public static class ShortenerServiceExtensions
{
    public static WebApplicationBuilder AddLinkEventConsumers(this WebApplicationBuilder builder)
    {
        builder.Services.AddHostedService<LinkCreatedConsumer>();
        builder.Services.AddHostedService<ClickTrackedConsumer>();
        return builder;
    }
}
