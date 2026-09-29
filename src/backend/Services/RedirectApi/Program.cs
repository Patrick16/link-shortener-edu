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
builder.AddRabbitMqPublisher();
builder.AddRedirectServices();

// A plain <a href> click to a short link is a top-level navigation, not subject to CORS — this is
// here for consistency with AuthApi/LinkApi and for any future script-initiated call (link preview,
// existence check, ...).
builder.Services.AddFrontendCors(builder.Configuration);

builder.Services.AddHealthChecks()
    .AddPostgresHealthCheck<DatabaseContext>()
    .AddRabbitMqHealthCheck();

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
