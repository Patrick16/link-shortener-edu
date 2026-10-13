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
builder.Services.AddSignalR();

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
