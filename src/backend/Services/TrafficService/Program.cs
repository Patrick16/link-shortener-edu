using Infrastructure;
using ServiceDefaults;
using TrafficService;
using WebDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

builder.AddPostgresDbContextFactory<DatabaseContext>();
builder.AddRabbitMqConsumer();
builder.AddClickTracking();

builder.Services.AddHealthChecks()
    .AddPostgresFactoryHealthCheck<DatabaseContext>()
    .AddRabbitMqHealthCheck()
    .AddMongoHealthCheck();

var app = builder.Build();

// This service owns clicks_db.
await app.MigratePostgresAsync<DatabaseContext>();

// The only reason this service has an HTTP listener at all - it doesn't serve any other endpoint.
// Ready means it can actually consume from RabbitMQ and write to Postgres right now.
app.MapHealthEndpoints();

await app.RunAsync();
