using Common;
using Common.Models;
using Infrastructure;
using ServiceDefaults;
using ShortenerService;
using WebDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

builder.AddPostgresDbContextFactory<DatabaseContext>();
builder.AddRedisDistributedCache();
builder.Services.AddSingleton<IEntityCacheService<Link>, LinkCacheService>();
builder.AddRabbitMqConsumer();
builder.AddLinkEventConsumers();

builder.Services.AddHealthChecks()
    .AddPostgresFactoryHealthCheck<DatabaseContext>()
    .AddRabbitMqHealthCheck();

var app = builder.Build();

// This service owns links_db (it's the only one that writes Links; LinkApi/RedirectApi only read the
// same table).
await app.MigratePostgresAsync<DatabaseContext>();

// The only reason this service has an HTTP listener at all - it doesn't serve any other endpoint.
// Ready means it can actually consume from RabbitMQ and write to Postgres right now.
app.MapHealthEndpoints();

await app.RunAsync();
