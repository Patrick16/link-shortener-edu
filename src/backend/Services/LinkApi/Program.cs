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
builder.AddRabbitMqPublisher();
builder.AddLinkServices();
builder.AddJwtAndInternalApiKeyAuthentication();
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
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHealthEndpoints();

app.Run();
