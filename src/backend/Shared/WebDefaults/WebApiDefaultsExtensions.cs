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

    // Opt-in, not folded into AddWebApiDefaults above - only LinkApi/RedirectApi call this (the two
    // services backlog item 5's scale-down demo targets, see PLAN.md's Resilience & Scale-Out
    // Backlog item 5), not AuthApi. Pairs with this service's own compose `stop_grace_period` (20s,
    // see docker-compose.yml) with a 5s margin for the process to actually exit once every
    // IHostedService - including Kestrel's own connection draining (stop accepting new connections,
    // let in-flight ones finish) - has stopped within this budget. Without an explicit value here,
    // ASP.NET Core's own 30s default outlives Compose's own 10s default `stop_grace_period`, so
    // Docker SIGKILLs the container before Kestrel's graceful drain even gets the chance to run out
    // (found while designing this item - the two unrelated defaults collided silently).
    public static WebApplicationBuilder AddGracefulShutdown(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(15));
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
