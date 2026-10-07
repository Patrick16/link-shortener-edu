namespace AuthApi.Models;

// Bound by hand from the "RateLimiting" appsettings.json section (Configuration.GetSection(...).Get<T>()
// in AuthApiServiceExtensions.AddAuthRateLimiting), not via IOptions<T> - this is read once at startup to
// build the rate limiter's policy, so there's no need for IOptions<T>'s DI/live-reload machinery (and
// nothing here goes through OptionsFactory<T>'s Activator.CreateInstance<T>() call, so the primary-ctor-
// record pitfall documented on BottleneckThresholds doesn't apply - this shape is just consistent with it).
public sealed record RateLimitingOptions
{
    public int PermitLimit { get; init; } = 100;
    public int WindowSeconds { get; init; } = 60;
}
