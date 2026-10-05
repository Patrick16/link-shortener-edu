using Infrastructure;
using ReportingService;
using ServiceDefaults;
using WebDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

builder.AddRabbitMqConsumer();
builder.AddClickHouseWriter();
builder.AddReportIngestion();

builder.Services.AddHealthChecks()
    .AddRabbitMqHealthCheck()
    .AddClickHouseHealthCheck<IClickFactStore>();

var app = builder.Build();

// No migration step here - unlike Postgres, the ClickHouse `clicks` table is created once by
// sandbox/infra/clickhouse/init.sql on the container's first boot, not by anything this service runs.

// The only reason this service has an HTTP listener at all - it doesn't serve any other endpoint.
// Ready means it can actually consume from RabbitMQ and write to ClickHouse right now.
app.MapHealthEndpoints();

await app.RunAsync();
