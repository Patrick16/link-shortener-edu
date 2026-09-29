using AuthApi;
using Infrastructure;
using ServiceDefaults;
using WebDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.AddWebApiDefaults();

builder.AddPostgresDbContextPool<DatabaseContext>();
builder.AddAuthServices();

// AllowCredentials is required for the browser to send/receive the refresh-token cookie cross-origin.
builder.Services.AddFrontendCors(builder.Configuration, allowCredentials: true);

builder.Services.AddHealthChecks()
    .AddPostgresHealthCheck<DatabaseContext>();

var app = builder.Build();

// This service owns users_db.
await app.MigratePostgresAsync<DatabaseContext>();

app.MapApiDocumentation();

// First in the pipeline so it can catch exceptions thrown by anything downstream.
app.UseApiExceptionHandling();
app.UseHttpsRedirection();
app.UseFrontendCors();
app.UseAuthorization();

app.MapControllers();
app.MapHealthEndpoints();

app.Run();
