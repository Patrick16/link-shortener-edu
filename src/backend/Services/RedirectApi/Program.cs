using Common;
using Infrastructure;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using RedirectApi;
using Scalar.AspNetCore;
using ServiceDefaults;
using StackExchange.Redis;
using WebDefaults;
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

// Separate from the IDistributedCache registration above (that's a JSON-blob GET/SET abstraction
// with no atomic increment) - this is the raw client the click counter needs for INCR.
builder.Services.AddSingleton<IConnectionMultiplexer>(
    _ => ConnectionMultiplexer.Connect(builder.Configuration.GetConnectionString(Constants.RedisConnectionString)!));
builder.Services.AddSingleton<IClickCounterService, RedisClickCounterService>();

var rabbitMqConnectionString = builder.Configuration.GetConnectionString(Constants.RabbitMqConnectionString);
var rabbitMqFallbackConnectionString = builder.Configuration.GetConnectionString(Constants.RabbitMqFallbackConnectionString);

builder.Services.AddSingleton<IRabbitMqConnection>(_ => new RabbitMqClient(rabbitMqConnectionString!));
builder.Services.AddSingleton<IMessageFallbackStore>(_ => new SqliteMessageFallbackStore(rabbitMqFallbackConnectionString!));
builder.Services.AddSingleton<IMessagePublisher, RabbitMqPublisher>();
builder.Services.AddHostedService<RabbitMqRetryWorker>();

builder.Services.AddApiExceptionHandling();
builder.Services.AddHealthChecks()
    .AddCheck<DbContextHealthCheck<DatabaseContext>>("database", tags: ["ready"])
    .AddCheck<RabbitMqHealthCheck>("rabbitmq", tags: ["ready"]);

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
