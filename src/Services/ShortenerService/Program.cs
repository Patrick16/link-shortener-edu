using ShortenerService;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<Worker>();
builder.Services.AddDbContextPool<DatabaseContext>(op => { });

var host = builder.Build();
host.Run();
