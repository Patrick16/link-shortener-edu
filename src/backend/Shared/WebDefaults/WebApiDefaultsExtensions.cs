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
    // see docker-compose.yml) with a 5s margin for the process to actually exit once this budget
    // has elapsed. Without an explicit value here, ASP.NET Core's own 30s default outlives
    // Compose's own 10s default `stop_grace_period`, so Docker SIGKILLs the container before
    // Kestrel's graceful drain (stop accepting new connections, let in-flight ones finish) even
    // gets the chance to run out (found while designing this item - the two unrelated defaults
    // collided silently).
    //
    // This 15s is ONE shared, sequential clock across every IHostedService.StopAsync call, not an
    // independent budget each gets - confirmed empirically (found during review): Kestrel's own
    // hosted service is registered last by Build() and so stops FIRST, draining in-flight HTTP
    // requests before LocalPublishQueueWorker/RabbitMqRetryWorker's own StopAsync is even called.
    // A slow-draining request landing at the same time as a nonempty LocalPublishQueue backlog
    // could leave the queue little of the 15s to drain by the time its turn comes - not a
    // regression (the old practical constraint was Compose's unenforced 10s default, smaller than
    // this), but worth knowing before assuming each hosted service gets its own 15s window.
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
