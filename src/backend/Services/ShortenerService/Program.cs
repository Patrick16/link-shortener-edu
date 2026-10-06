using Common;
using Common.Models;
using Infrastructure;
using ServiceDefaults;
using ShortenerService;
using WebDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.AddGrpcKestrelEndpoint();

builder.AddPostgresDbContextFactory<DatabaseContext>();
builder.AddRedisDistributedCache();
builder.Services.AddSingleton<IEntityCacheService<Link>, LinkCacheService>();
builder.AddRabbitMqConsumer();
builder.AddLinkEventConsumers();
// Always hosted - see GrpcMessagingExtensions.AddMessagingGrpcServer's own comment for why this
// isn't itself gated on Messaging:Mode.
builder.AddMessagingGrpcServer();

builder.Services.AddHealthChecks()
    .AddPostgresFactoryHealthCheck<DatabaseContext>()
    .AddRabbitMqHealthCheck();

var app = builder.Build();

// This service owns links_db (it's the only one that writes Links; LinkApi/RedirectApi only read the
// same table).
await app.MigratePostgresAsync<DatabaseContext>();

// The only reason this service has an HTTP listener at all beyond health/gRPC - it doesn't serve
// any other REST endpoint. Ready means it can actually consume from RabbitMQ and write to Postgres
// right now.
app.MapHealthEndpoints();
app.MapGrpcService<MessagingGrpcService>();

await app.RunAsync();
