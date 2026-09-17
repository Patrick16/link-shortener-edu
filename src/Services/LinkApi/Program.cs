using Common;
using Common.Models;
using Infrastructure;
using LinkApi;
using Microsoft.EntityFrameworkCore;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

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

builder.Services.AddStackExchangeRedisCache(options =>
{
    options.Configuration = builder.Configuration.GetConnectionString(Constants.RedisConnectionString);
    options.InstanceName = builder.Configuration[Constants.RedisInstanceName];
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
