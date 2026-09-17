using Common;
using Infrastructure;
using Microsoft.EntityFrameworkCore;
using ShortenerService;

var builder = Host.CreateApplicationBuilder(args);

var connectionString = builder.Configuration.GetConnectionString(Constants.PostgresConnectionString);
builder.Services.AddPooledDbContextFactory<DatabaseContext>(
    op => op.UseNpgsql(connectionString, options =>
    {
        options.EnableRetryOnFailure(3, TimeSpan.FromSeconds(4L), null);
    }));

var rabbitMqConnectionString = builder.Configuration.GetConnectionString(Constants.RabbitMqConnectionString);
builder.Services.AddSingleton<IRabbitMqConnection>(_ => new RabbitMqClient(rabbitMqConnectionString!));
builder.Services.AddSingleton<IMessageConsumer, RabbitMqConsumer>();

builder.Services.AddHostedService<LinkCreatedConsumer>();

var host = builder.Build();

// Apply pending EF Core migrations on startup — this service owns the shortener-service schema
// (it's the only one that writes Links; LinkApi/RedirectApi only read the same table).
await using (var scope = host.Services.CreateAsyncScope())
{
    var dbContextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<DatabaseContext>>();
    await using var db = await dbContextFactory.CreateDbContextAsync();
    await db.Database.MigrateAsync();
}

await host.RunAsync();
