using Serilog;

namespace ControlApi.Extensions;

public static class LoggingExtensions
{
    // Same one-line console format as the 5 services in src/backend/Shared/ServiceDefaults - control-api
    // isn't part of that solution (it's sandbox tooling, not a shipped service) so it can't reference
    // that project directly, but logs from both should look the same when you're watching `docker
    // compose logs` across the whole stack. No OTel sink here (unlike ServiceDefaults) - control-api
    // doesn't export its own logs via OTLP, it only ingests trace batches otel-collector forwards to it.
    public static WebApplicationBuilder AddConsoleLogging(this WebApplicationBuilder builder)
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Service", builder.Environment.ApplicationName)
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Service}/{SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        builder.Logging.ClearProviders();
        builder.Services.AddSerilog();
        return builder;
    }
}
