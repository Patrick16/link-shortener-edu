using Common;
using Infrastructure;
using Microsoft.EntityFrameworkCore;
using ShortenerService;

var builder = Host.CreateApplicationBuilder(args);

var connectionString = builder.Configuration.GetConnectionString(Constants.PostgresConnectionString);
builder.Services.AddPooledDbContextFactory<DatabaseContext>(
    op => op.UseNpgsql(connectionString, options =>
    {
        options.EnableRetryOnFailure(3, TimeSpan.FromSeconds(4L), null);
    }));

var rabbitMqConnectionString = builder.Configuration.GetConnectionString(Constants.RabbitMqConnectionString);
builder.Services.AddSingleton<IRabbitMqConnection>(_ => new RabbitMqClient(rabbitMqConnectionString!));
builder.Services.AddSingleton<IMessageConsumer, RabbitMqConsumer>();

builder.Services.AddHostedService<LinkCreatedConsumer>();

var host = builder.Build();
host.Run();
