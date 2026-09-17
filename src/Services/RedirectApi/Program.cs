using Common;
using Common.Models;
using Microsoft.EntityFrameworkCore;
using RedirectApi;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

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
