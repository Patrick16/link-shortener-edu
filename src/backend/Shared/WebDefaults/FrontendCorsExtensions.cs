using Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace WebDefaults;

public static class FrontendCorsExtensions
{
    // Allows the frontend origins from Cors:AllowedOrigins (dev-server default when unset).
    // allowCredentials is required for the browser to send/receive a cookie cross-origin (AuthApi's
    // refresh-token cookie); it only works with an explicit origin list, never "*", which this always is.
    public static IServiceCollection AddFrontendCors(
        this IServiceCollection services, IConfiguration configuration, bool allowCredentials = false)
    {
        var origins = configuration.GetSection(Constants.CorsAllowedOriginsSection).Get<string[]>()
            ?? ["http://localhost:5173"];

        services.AddCors(options =>
        {
            options.AddPolicy(Constants.FrontendCorsPolicy, policy =>
            {
                policy.WithOrigins(origins).AllowAnyMethod().AllowAnyHeader();
                if (allowCredentials)
                {
                    policy.AllowCredentials();
                }
            });
        });
        return services;
    }

    public static IApplicationBuilder UseFrontendCors(this IApplicationBuilder app)
        => app.UseCors(Constants.FrontendCorsPolicy);
}
