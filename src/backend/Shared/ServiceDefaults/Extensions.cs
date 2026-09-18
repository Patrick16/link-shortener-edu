using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace ServiceDefaults;

// Shared OpenTelemetry wiring (logs, metrics, traces) for every service in this solution — the
// lightweight half of the Aspire pattern. Ships data via OTLP to the Aspire Dashboard container
// (see docker-compose.yml); doesn't include the AppHost/orchestration half, since docker-compose
// stays the orchestrator here (see .notes/PLAN.md for why).
public static class Extensions
{
    // Names of custom ActivitySources this solution's own code creates (e.g. for RabbitMQ
    // publish/consume spans) that wouldn't otherwise be picked up by the instrumentation packages
    // below. Add to this list — see Infrastructure/MessagingActivitySource.cs.
    private static readonly string[] AdditionalActivitySources = ["LinkShortener.Messaging"];

    public static IHostApplicationBuilder AddServiceDefaults(this IHostApplicationBuilder builder)
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

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
        var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        return builder;
    }
}
