using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Scalar.AspNetCore;

namespace WebDefaults;

// The baseline every controller-based API (AuthApi, LinkApi, RedirectApi) starts from.
public static class WebApiDefaultsExtensions
{
    // Controllers + OpenAPI document + uniform error responses (see ApiExceptionHandlingExtensions) +
    // ASP.NET Core telemetry. Call right after AddServiceDefaults().
    public static WebApplicationBuilder AddWebApiDefaults(this WebApplicationBuilder builder)
    {
        // AspNetCore OTel instrumentation lives here, not in ServiceDefaults - see the comment in
        // ServiceDefaults.csproj for why (worker services can't carry an ASP.NET Core dependency).
        builder.Services.ConfigureOpenTelemetryMeterProvider(metrics => metrics.AddAspNetCoreInstrumentation());
        builder.Services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddAspNetCoreInstrumentation());

        builder.Services.AddControllers();
        // Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
        builder.Services.AddOpenApi();
        builder.Services.AddApiExceptionHandling();
        return builder;
    }

    // OpenAPI document + Scalar UI, development only.
    public static WebApplication MapApiDocumentation(this WebApplication app)
    {
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
            app.MapScalarApiReference();
        }

        return app;
    }
}
