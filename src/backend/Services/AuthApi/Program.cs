using AuthApi;
using Infrastructure;
using ServiceDefaults;
using WebDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.AddWebApiDefaults();

builder.AddPostgresDbContextPool<DatabaseContext>();
builder.AddAuthServices();
builder.AddAuthRateLimiting();

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
// AddAuthRateLimiting's own OnRejected writes the 429's ProblemDetails body directly (see its
// comment), so this doesn't depend on UseApiExceptionHandling's ordering above - placed here simply
// to match the usual auth-then-rate-limit pipeline order.
app.UseRateLimiter();
app.UseAuthorization();

app.MapControllers();
app.MapHealthEndpoints();

app.Run();
