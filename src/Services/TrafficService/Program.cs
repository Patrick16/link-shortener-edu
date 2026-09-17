using Common;
using Microsoft.EntityFrameworkCore;
using TrafficService;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<Worker>();

var connectionString = builder.Configuration.GetConnectionString(Constants.PostgresConnectionString);
builder.Services.AddPooledDbContextFactory<DatabaseContext>(
    op => op.UseNpgsql(connectionString, options =>
    {
        options.EnableRetryOnFailure(3, TimeSpan.FromSeconds(4L), null);
    }));

var host = builder.Build();
host.Run();
