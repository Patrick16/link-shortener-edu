using Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Infrastructure;

public static class RabbitMqExtensions
{
    // Publish-only services (LinkApi, RedirectApi): connection + durable SQLite fallback for when the
    // broker is unreachable + the retry worker that drains it. The warmup service opens the
    // connection during startup so the first request doesn't pay for it (see RabbitMqWarmupService).
    public static IHostApplicationBuilder AddRabbitMqPublisher(this IHostApplicationBuilder builder)
    {
        builder.AddRabbitMqConnection();

        var fallbackConnectionString = builder.Configuration.GetConnectionString(Constants.RabbitMqFallbackConnectionString);
        builder.Services.AddSingleton<IMessageFallbackStore>(_ => new SqliteMessageFallbackStore(fallbackConnectionString!));
        builder.Services.AddSingleton<IMessagePublisher, RabbitMqPublisher>();
        builder.Services.AddHostedService<RabbitMqWarmupService>();
        builder.Services.AddHostedService<RabbitMqRetryWorker>();
        return builder;
    }

    // Consume-only services (ShortenerService, TrafficService). Register their own
    // AddHostedService<...Consumer>() on top of this.
    public static IHostApplicationBuilder AddRabbitMqConsumer(this IHostApplicationBuilder builder)
    {
        builder.AddRabbitMqConnection();
        builder.Services.AddSingleton<IMessageConsumer, RabbitMqConsumer>();
        return builder;
    }

    private static void AddRabbitMqConnection(this IHostApplicationBuilder builder)
    {
        var connectionString = builder.Configuration.GetConnectionString(Constants.RabbitMqConnectionString);
        builder.Services.AddSingleton<IRabbitMqConnection>(_ => new RabbitMqClient(connectionString!));
    }
}
