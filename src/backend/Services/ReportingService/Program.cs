using Infrastructure;
using ReportingService;
using ServiceDefaults;
using WebDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

builder.AddRabbitMqConsumer();
builder.AddClickHouseWriter();
builder.AddReportIngestion();
// Unlike ShortenerService/TrafficService, this service does NOT host a messaging-mode gRPC
// endpoint - RedirectApi's SyncGrpcDispatcher targets TrafficService only (see its own comment),
// so there was never a caller for one. An earlier version hosted it anyway "for symmetry"; removed
// during review as dead code with no test coverage and no caller (found during review) - add it
// back in the same change that actually wires a client to call it.

builder.Services.AddHealthChecks()
    .AddRabbitMqHealthCheck()
    .AddClickHouseHealthCheck<IClickFactStore>();

var app = builder.Build();

// No migration step here - unlike Postgres, the ClickHouse `clicks` table is created once by
// sandbox/infra/clickhouse/init.sql on the container's first boot, not by anything this service runs.

// The only reason this service has an HTTP listener at all - it doesn't serve any other REST
// endpoint. Ready means it can actually consume from RabbitMQ and write to ClickHouse right now.
app.MapHealthEndpoints();

await app.RunAsync();
