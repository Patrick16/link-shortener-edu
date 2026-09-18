using System.Text;
using Common;
using Infrastructure;
using LinkApi;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
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
    op => op.UseNpgsql(connectionString, options=>
    {
        options.EnableRetryOnFailure(3, TimeSpan.FromSeconds(4L), null);
    }));

builder.Services.AddSingleton<IEntityCacheService<Link>, LinkCacheService>();

var rabbitMqConnectionString = builder.Configuration.GetConnectionString(Constants.RabbitMqConnectionString);
var rabbitMqFallbackConnectionString = builder.Configuration.GetConnectionString(Constants.RabbitMqFallbackConnectionString);

builder.Services.AddSingleton<IRabbitMqConnection>(_ => new RabbitMqClient(rabbitMqConnectionString!));
builder.Services.AddSingleton<IMessageFallbackStore>(_ => new SqliteMessageFallbackStore(rabbitMqFallbackConnectionString!));
builder.Services.AddSingleton<IMessagePublisher, RabbitMqPublisher>();
builder.Services.AddSingleton<IHashGenerator, Sha256Base62HashGenerator>();
builder.Services.AddHostedService<RabbitMqRetryWorker>();

builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = builder.Configuration.GetConnectionString(Constants.RedisConnectionString);
    options.InstanceName = builder.Configuration[Constants.RedisInstanceName];
});

var corsOrigins = builder.Configuration.GetSection(Constants.CorsAllowedOriginsSection).Get<string[]>()
    ?? ["http://localhost:5173"];
builder.Services.AddCors(options =>
{
    options.AddPolicy(Constants.FrontendCorsPolicy, policy =>
        policy.WithOrigins(corsOrigins).AllowAnyMethod().AllowAnyHeader());
});

// Authentication is optional here — nothing in this service has [Authorize], so an anonymous
// request is never rejected. This only lets CreateLink read the caller's userId from a valid
// Bearer token when one is present. Same signing key/issuer/audience config keys AuthApi issues
// with (see Common.Constants) — they have to match or every token would fail validation here.
var jwtSigningKey = builder.Configuration[Constants.JwtSigningKeySection];
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        // Without this, the handler silently renames the "sub" claim to the legacy
        // ClaimTypes.NameIdentifier URI, and User.FindFirst(JwtRegisteredClaimNames.Sub) in
        // LinksController would never find it. Keep claim types exactly as the token declares them.
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = builder.Configuration[Constants.JwtIssuerSection],
            ValidateAudience = true,
            ValidAudience = builder.Configuration[Constants.JwtAudienceSection],
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSigningKey ?? string.Empty)),
        };
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

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
