namespace AuthApi;

// AuthApi-only wiring - the parts of Program.cs that no other service shares.
public static class AuthApiServiceExtensions
{
    public static WebApplicationBuilder AddAuthServices(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IJwtTokenGenerator, JwtTokenGenerator>();
        // Scoped, not Singleton - it depends on the pooled (scoped) DatabaseContext.
        builder.Services.AddScoped<IRefreshTokenService, RefreshTokenService>();
        builder.Services.AddHostedService<RefreshTokenCleanupWorker>();
        return builder;
    }
}
