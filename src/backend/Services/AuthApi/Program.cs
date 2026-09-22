using AuthApi;
using Common;
using Infrastructure;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Scalar.AspNetCore;
using ServiceDefaults;
using WebDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

// AspNetCore OTel instrumentation lives here, not in ServiceDefaults — see the comment in
// ServiceDefaults.csproj for why (worker services can't carry an ASP.NET Core dependency).
builder.Services.ConfigureOpenTelemetryMeterProvider(metrics => metrics.AddAspNetCoreInstrumentation());
builder.Services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddAspNetCoreInstrumentation());

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var connectionString = builder.Configuration.GetConnectionString(Constants.PostgresConnectionString);
builder.Services.AddDbContextPool<DatabaseContext>(
    op => op.UseNpgsql(connectionString, options =>
    {
        options.EnableRetryOnFailure(3, TimeSpan.FromSeconds(4L), null);
    }));

builder.Services.AddSingleton<IJwtTokenGenerator, JwtTokenGenerator>();
// Scoped, not Singleton - it depends on the pooled (scoped) DatabaseContext.
builder.Services.AddScoped<IRefreshTokenService, RefreshTokenService>();

builder.Services.AddApiExceptionHandling();
builder.Services.AddHealthChecks()
    .AddCheck<DbContextHealthCheck<DatabaseContext>>("database", tags: ["ready"]);

var corsOrigins = builder.Configuration.GetSection(Constants.CorsAllowedOriginsSection).Get<string[]>()
    ?? ["http://localhost:5173"];
builder.Services.AddCors(options =>
{
    options.AddPolicy(Constants.FrontendCorsPolicy, policy =>
        // AllowCredentials is required for the browser to send/receive the refresh-token cookie
        // cross-origin; it only works with an explicit origin list (never AllowAnyOrigin/"*"),
        // which corsOrigins already is.
        policy.WithOrigins(corsOrigins).AllowAnyMethod().AllowAnyHeader().AllowCredentials());
});

var app = builder.Build();

// Apply pending EF Core migrations on startup — this service owns users_db. Connects directly to
// the primary, bypassing PgCat, for this call specifically (see Constants.PostgresPrimaryConnectionString).
var migrationConnectionString = builder.Configuration.GetConnectionString(Constants.PostgresPrimaryConnectionString) ?? connectionString;
await using (var migrationContext = new DatabaseContext(new DbContextOptionsBuilder<DatabaseContext>().UseNpgsql(migrationConnectionString).Options))
{
    await migrationContext.Database.MigrateAsync();
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

// First in the pipeline so it can catch exceptions thrown by anything downstream.
app.UseExceptionHandler();

app.UseHttpsRedirection();

app.UseCors(Constants.FrontendCorsPolicy);

app.UseAuthorization();

app.MapControllers();

// Liveness: the process can respond at all - no dependency checks. Readiness: can it actually
// serve traffic right now - runs the "ready"-tagged checks registered above.
app.MapHealthChecks("/health/live", new() { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new() { Predicate = check => check.Tags.Contains("ready") });

app.Run();
