using Common;
using Infrastructure;
using Microsoft.EntityFrameworkCore;
using ServiceDefaults;
using TrafficService;

var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();

var connectionString = builder.Configuration.GetConnectionString(Constants.PostgresConnectionString);
builder.Services.AddPooledDbContextFactory<DatabaseContext>(
    op => op.UseNpgsql(connectionString, options =>
    {
        options.EnableRetryOnFailure(3, TimeSpan.FromSeconds(4L), null);
    }));

var rabbitMqConnectionString = builder.Configuration.GetConnectionString(Constants.RabbitMqConnectionString);
builder.Services.AddSingleton<IRabbitMqConnection>(_ => new RabbitMqClient(rabbitMqConnectionString!));
builder.Services.AddSingleton<IMessageConsumer, RabbitMqConsumer>();

builder.Services.AddHostedService<ClickTrackedConsumer>();

var host = builder.Build();

// Apply pending EF Core migrations on startup — this service owns the traffic-service schema.
await using (var scope = host.Services.CreateAsyncScope())
{
    var dbContextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<DatabaseContext>>();
    await using var db = await dbContextFactory.CreateDbContextAsync();
    await db.Database.MigrateAsync();
}

await host.RunAsync();
