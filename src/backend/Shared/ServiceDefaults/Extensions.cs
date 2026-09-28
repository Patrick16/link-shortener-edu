using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

namespace ServiceDefaults;

// Shared OpenTelemetry + logging wiring for every service in this solution — the lightweight half
// of the Aspire pattern. Ships traces/metrics/logs via OTLP to the Aspire Dashboard container (see
// docker-compose.yml); doesn't include the AppHost/orchestration half, since docker-compose stays
// the orchestrator here (see .notes/PLAN.md for why).
public static class Extensions
{
    // Names of custom ActivitySources this solution's own code creates (e.g. for RabbitMQ
    // publish/consume spans) that wouldn't otherwise be picked up by the instrumentation packages
    // below. Add to this list — see Infrastructure/MessagingActivitySource.cs.
    private static readonly string[] AdditionalActivitySources = ["LinkShortener.Messaging"];

    public static IHostApplicationBuilder AddServiceDefaults(this IHostApplicationBuilder builder)
    {
        var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        ConfigureLogging(builder, otlpEndpoint);

        // ASP.NET Core instrumentation is deliberately not added here — see the comment in
        // ServiceDefaults.csproj. The three web APIs (AuthApi, LinkApi, RedirectApi) add it
        // themselves, right after calling this method, via ConfigureOpenTelemetryMeterProvider/
        // ConfigureOpenTelemetryTracerProvider — that's how you extend a pipeline already built
        // here without re-registering it.
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(builder.Environment.ApplicationName))
            .WithMetrics(metrics => metrics
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation())
            .WithTracing(tracing =>
            {
                tracing.AddHttpClientInstrumentation();

                foreach (var source in AdditionalActivitySources)
                {
                    tracing.AddSource(source);
                }
            });

        // Only wire up OTLP export when an endpoint is actually configured (docker-compose sets
        // OTEL_EXPORTER_OTLP_ENDPOINT — see docker-compose.yml). Running a service outside compose
        // (e.g. `dotnet run` against local infra) works the same, just without telemetry going anywhere.
        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        return builder;
    }

    // Serilog replaces the default logging providers here so every service (all 3 web APIs, both
    // workers) renders the exact same one-line console format and, when running under docker-compose,
    // ships the same log events to the Aspire Dashboard as its traces/metrics. This only changes HOW
    // a log line that gets through is rendered/shipped - it deliberately does NOT set its own minimum
    // level, so appsettings.json's existing "Logging:LogLevel" section keeps deciding WHICH lines get
    // through, exactly as it did before Serilog existed here. That's also the lever for the
    // Debug-level per-request/per-message flow logs sprinkled through the controllers/consumers: they
    // stay silent under the "Information" default (so a k6 load test's log output isn't drowned in
    // one line per request), and only appear once a service's category is flipped to "Debug" in
    // appsettings.Development.json or an environment override.
    private static void ConfigureLogging(IHostApplicationBuilder builder, string? otlpEndpoint)
    {
        var serviceName = builder.Environment.ApplicationName;

        var loggerConfiguration = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Service", serviceName)
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Service}/{SourceContext}: {Message:lj}{NewLine}{Exception}");

        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            loggerConfiguration = loggerConfiguration.WriteTo.OpenTelemetry(options =>
            {
                options.Endpoint = otlpEndpoint;
                options.Protocol = global::Serilog.Sinks.OpenTelemetry.OtlpProtocol.Grpc;
                options.ResourceAttributes = new Dictionary<string, object> { ["service.name"] = serviceName };
            });
        }

        Log.Logger = loggerConfiguration.CreateLogger();

        builder.Logging.ClearProviders();
        builder.Services.AddSerilog();
    }
}
