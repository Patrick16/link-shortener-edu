using Common;
using Infrastructure;
using Microsoft.EntityFrameworkCore;
using ServiceDefaults;
using ShortenerService;

var builder = WebApplication.CreateBuilder(args);
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

builder.Services.AddHostedService<LinkCreatedConsumer>();
builder.Services.AddHostedService<ClickTrackedConsumer>();

builder.Services.AddHealthChecks()
    .AddCheck<DbContextFactoryHealthCheck<DatabaseContext>>("database", tags: ["ready"])
    .AddCheck<RabbitMqHealthCheck>("rabbitmq", tags: ["ready"]);

var app = builder.Build();

// Apply pending EF Core migrations on startup — this service owns links_db (it's the only one that
// writes Links; LinkApi/RedirectApi only read the same table). Connects directly to the primary,
// bypassing PgCat, for this call specifically (see Constants.PostgresPrimaryConnectionString).
var migrationConnectionString = builder.Configuration.GetConnectionString(Constants.PostgresPrimaryConnectionString) ?? connectionString;
await using (var migrationContext = new DatabaseContext(new DbContextOptionsBuilder<DatabaseContext>().UseNpgsql(migrationConnectionString).Options))
{
    await migrationContext.Database.MigrateAsync();
}

// The only reason this service has an HTTP listener at all - it doesn't serve any other endpoint.
// Liveness: the process can respond at all - no dependency checks. Readiness: can it actually
// consume from RabbitMQ and write to Postgres right now - runs the "ready"-tagged checks above.
app.MapHealthChecks("/health/live", new() { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new() { Predicate = check => check.Tags.Contains("ready") });

await app.RunAsync();
