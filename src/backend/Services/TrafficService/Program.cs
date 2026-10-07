using Infrastructure;
using ServiceDefaults;
using TrafficService;
using WebDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.AddGrpcKestrelEndpoint();

builder.AddPostgresDbContextFactory<DatabaseContext>();
builder.AddRabbitMqConsumer();
builder.AddClickTracking();
// Always hosted - see GrpcMessagingExtensions.AddMessagingGrpcServer's own comment for why this
// isn't itself gated on Messaging:Mode.
builder.AddMessagingGrpcServer();

builder.Services.AddHealthChecks()
    .AddPostgresFactoryHealthCheck<DatabaseContext>()
    .AddRabbitMqHealthCheck()
    .AddMongoHealthCheck();

var app = builder.Build();

// This service owns both clicks_db (Postgres migration) and clicks_meta_db (Mongo TTL index) -
// independent databases with no ordering dependency between them, so they run concurrently instead
// of paying the sum of both round trips before health endpoints are even mapped below.
await Task.WhenAll(
    app.MigratePostgresAsync<DatabaseContext>(),
    app.Services.GetRequiredService<IClickMetaStore>().EnsureIndexesAsync());

// The only reason this service has an HTTP listener at all beyond health/gRPC - it doesn't serve
// any other REST endpoint. Ready means it can actually consume from RabbitMQ and write to Postgres
// right now.
app.MapHealthEndpoints();
app.MapGrpcService<MessagingGrpcService>();

await app.RunAsync();
