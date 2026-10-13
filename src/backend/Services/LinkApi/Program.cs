using Common;
using Infrastructure;
using LinkApi;
using ServiceDefaults;
using WebDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.AddWebApiDefaults();

builder.AddPostgresDbContextPool<DatabaseContext>();
builder.AddRedisDistributedCache();

// RabbitMQ's publish side (connection, SQLite fallback, warmup/retry workers, health check) is
// only needed in the default async/bus mode - in messaging-mode=grpc nothing ever resolves
// ILocalPublishQueue/IMessagePublisher (see AddEventDispatcher below), so skip the connection and
// its background workers entirely instead of paying their startup cost for an unused transport
// (found during review).
var isGrpcMode = builder.Configuration.IsMessagingGrpcMode();
if (!isGrpcMode)
{
    builder.AddRabbitMqPublisher();
}
// LinksController depends on IEventDispatcher, not ILocalPublishQueue directly - which concrete
// class backs it (AsyncQueueDispatcher vs SyncGrpcDispatcher) is Messaging:Mode's whole job.
builder.AddEventDispatcher();
builder.AddLinkServices();
builder.AddJwtAndInternalApiKeyAuthentication();
builder.Services.AddFrontendCors(builder.Configuration);

var healthChecks = builder.Services.AddHealthChecks()
    .AddPostgresHealthCheck<DatabaseContext>();
if (isGrpcMode)
{
    // Without this, /health/ready stayed Healthy on Postgres alone while SyncGrpcDispatcher's every
    // call to a downstream ShortenerService/TrafficService that's actually unreachable failed with
    // 502/503 (found during review - see GrpcChannelHealthCheck).
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
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHealthEndpoints();

app.Run();
