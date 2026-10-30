using Infrastructure;
using Microsoft.AspNetCore.HttpOverrides;
using RedirectApi;
using ServiceDefaults;
using WebDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.AddWebApiDefaults();
builder.AddGracefulShutdown();

builder.AddPostgresDbContextPool<DatabaseContext>();
builder.AddRedisDistributedCache();
builder.AddClickCounter();
builder.AddLinkDeletionMarkerStore();

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
builder.AddRedirectRateLimiting();

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

// Must run before anything reads HttpContext.Connection.RemoteIpAddress - RedisRateLimitFilter
// (via RedirectController's [ServiceFilter], see AddRedirectRateLimiting) is the first and only
// consumer of that today, so this is placed first in the pipeline per Microsoft's own guidance.
// ForwardLimit = 1 means only the LAST (nginx-appended) entry in X-Forwarded-For is ever honored -
// a client that prepends its own fake entries into the header before nginx ever sees it gets
// ignored, not trusted (see AddRedirectRateLimiting's own comment on why this matters). KnownNetworks/
// KnownProxies are cleared, not pinned to a specific container IP: RedirectApi has no host port
// mapping (nginx is the only way in from outside the Docker network - see docker-compose.yml's own
// comment on this service), and Docker Compose doesn't give nginx's container a fixed, predictable
// address across runs to pin to instead.
var forwardedHeadersOptions = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor, ForwardLimit = 1 };
forwardedHeadersOptions.KnownNetworks.Clear();
forwardedHeadersOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeadersOptions);

// Still early enough to catch exceptions thrown by anything downstream of it - only
// UseForwardedHeaders above needs to run earlier than this.
app.UseApiExceptionHandling();
app.UseHttpsRedirection();
app.UseFrontendCors();

app.MapControllers();
app.MapHealthEndpoints();

app.Run();
