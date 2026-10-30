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
    .AddPostgresHealthCheck<DatabaseContext>()
    // Found in review: AuthApi's rate limiter depends on Redis (AddAuthRateLimiting ->
    // AddRedisRateLimiter -> AddRedisMultiplexer) but /health/ready never reflected that - only
    // needs the multiplexer AddRedisMultiplexer already registers, not AddRedisDistributedCache.
    .AddRedisHealthCheck();

var app = builder.Build();

// This service owns users_db.
await app.MigratePostgresAsync<DatabaseContext>();

app.MapApiDocumentation();

// First in the pipeline so it can catch exceptions thrown by anything downstream.
app.UseApiExceptionHandling();
app.UseHttpsRedirection();
app.UseFrontendCors();
// No app.UseRateLimiter() - AddAuthRateLimiting now wires a RedisRateLimitFilter via
// [ServiceFilter] on AuthController itself (an MVC action filter, not global middleware), so
// there's nothing to add to the pipeline here.
app.UseAuthorization();

app.MapControllers();
app.MapHealthEndpoints();

app.Run();
