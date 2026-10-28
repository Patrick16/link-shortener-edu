using Common;
using Infrastructure;
using ReportingApi.Realtime;
using ServiceDefaults;
using WebDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.AddWebApiDefaults();

builder.AddClickHouseReader();

// Unlike LinkApi/RedirectApi's optional-JWT posture, every ReportingApi endpoint requires a valid
// token - this is the one read path that would otherwise let anyone enumerate click stats for any
// hash with no account at all (found during review). AddJwtBearerAuthentication alone, no
// InternalApiKey scheme - nothing internal calls this API, only the product frontend.
builder.AddJwtBearerAuthentication();
builder.Services.AddFrontendCors(builder.Configuration);

// The per-user top-links view: rolling day buckets written by RedirectApi, read here and pushed to
// open dashboards over SignalR as clicks happen.
builder.AddRedisMultiplexer();
builder.Services.AddSingleton<ITopLinksReader, RedisTopLinksReader>();
builder.Services.AddSingleton<TopLinksSubscriptions>();
builder.Services.AddSingleton<TopLinksPushScheduler>();
builder.Services.AddHostedService<TopLinksPushService>();

// Redis backplane (backlog item 11) - ReportingApi can now run as more than one replica (see
// nginx.conf's reporting_api_upstream + ContainerLifecycleService.ScalableServices), and
// TopLinksHub.Clients.Group(...)/IHubContext<TopLinksHub>.Clients.Group(...) (in
// TopLinksPushService) only ever reach connections known to the SAME process without this - the
// same connection string every other Redis client in this service already uses
// (AddRedisMultiplexer below), not a second config entry to keep in sync.
builder.Services.AddSignalR()
    .AddStackExchangeRedis(builder.Configuration.GetConnectionString(Constants.RedisConnectionString)!);

builder.Services.AddHealthChecks()
    .AddClickHouseHealthCheck<IClickFactQueryService>()
    .AddRedisHealthCheck();

var app = builder.Build();

app.MapApiDocumentation();

app.UseApiExceptionHandling();
app.UseHttpsRedirection();
app.UseFrontendCors();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<TopLinksHub>("/hub/top-links");
app.MapHealthEndpoints();

app.Run();
