using Infrastructure;
using RedirectApi;
using ServiceDefaults;
using WebDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.AddWebApiDefaults();

builder.AddPostgresDbContextPool<DatabaseContext>();
builder.AddRedisDistributedCache();
builder.AddClickCounter();

// RabbitMQ's publish side (connection, SQLite fallback, warmup/retry workers, health check) is
// only needed in the default async/bus mode - see LinkApi/Program.cs's identical comment (found
// during review).
var isGrpcMode = builder.Configuration.IsMessagingGrpcMode();
if (!isGrpcMode)
{
    builder.AddRabbitMqPublisher();
}
// RedirectController depends on IEventDispatcher, not ILocalPublishQueue directly - which
// concrete class backs it (AsyncQueueDispatcher vs SyncGrpcDispatcher) is Messaging:Mode's whole
// job. gRPC mode's target is TrafficService only - see RedirectController's own comment for the
// resulting asymmetry (ShortenerService/ReportingService's consumers go stale in this mode).
builder.AddEventDispatcher();
builder.AddRedirectServices();

// A plain <a href> click to a short link is a top-level navigation, not subject to CORS — this is
// here for consistency with AuthApi/LinkApi and for any future script-initiated call (link preview,
// existence check, ...).
builder.Services.AddFrontendCors(builder.Configuration);

var healthChecks = builder.Services.AddHealthChecks()
    .AddPostgresHealthCheck<DatabaseContext>();
if (isGrpcMode)
{
    // Without this, /health/ready stayed Healthy on Postgres alone while SyncGrpcDispatcher's every
    // call to a downstream TrafficService that's actually unreachable failed with 502/503 (found
    // during review - see GrpcChannelHealthCheck).
    healthChecks.AddGrpcMessagingHealthCheck();
}
else
{
    healthChecks.AddRabbitMqHealthCheck();
}

var app = builder.Build();

app.MapApiDocumentation();

// First in the pipeline so it can catch exceptions thrown by anything downstream.
app.UseApiExceptionHandling();
app.UseHttpsRedirection();
app.UseFrontendCors();
app.UseAuthorization();

app.MapControllers();
app.MapHealthEndpoints();

app.Run();
