using Common;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using RedirectApi;
using Scalar.AspNetCore;
using ServiceDefaults;
// OpenTelemetry.Trace also has a "Link" type (a span link) — alias ours to avoid the clash.
using Link = Common.Models.Link;

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

builder.Services.AddSingleton<IEntityCacheService<Link>, LinkCacheService>();

builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = builder.Configuration.GetConnectionString(Constants.RedisConnectionString);
    options.InstanceName = builder.Configuration[Constants.RedisInstanceName];
});

// A plain <a href> click to a short link is a top-level navigation, not subject to CORS — this is
// here for consistency with AuthApi/LinkApi and for any future script-initiated call (link preview,
// existence check, ...).
var corsOrigins = builder.Configuration.GetSection(Constants.CorsAllowedOriginsSection).Get<string[]>()
    ?? ["http://localhost:5173"];
builder.Services.AddCors(options =>
{
    options.AddPolicy(Constants.FrontendCorsPolicy, policy =>
        policy.WithOrigins(corsOrigins).AllowAnyMethod().AllowAnyHeader());
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseHttpsRedirection();

app.UseCors(Constants.FrontendCorsPolicy);

app.UseAuthorization();

app.MapControllers();

app.Run();
