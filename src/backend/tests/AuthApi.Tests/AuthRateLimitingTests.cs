using AuthApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WebDefaults;

namespace AuthApi.Tests;

// AddAuthRateLimiting now wires the shared RedisRateLimitFilter (WebDefaults) instead of
// Microsoft.AspNetCore.RateLimiting's own middleware - see that class's own comment for why. What's
// actually AuthApi-specific here is just the RateLimitPolicy it builds (partition-key selector,
// numbers, FailClosed) - the Redis-check-and-reject mechanics themselves are shared and tested
// once, generically, in Integration.Tests/RedisRateLimitFilterTests.cs and
// Infrastructure.Tests/RedisRateLimiterTests.cs.
public class AuthRateLimitingTests
{
    private static RateLimitPolicy BuildPolicy(int? permitLimit = null, int? windowSeconds = null)
    {
        var builder = WebApplication.CreateBuilder();
        // Clear what WebApplication.CreateBuilder() auto-loaded (AuthApi's own real
        // appsettings.json, env vars, ...) - this test needs full control over exactly what's
        // configured, not whatever happens to be sitting in this project's own output directory
        // (AuthApi's real defaults - 100/60s - happen to match RateLimitingOptions' own record
        // defaults, which would otherwise mask this test actually exercising the fallback path).
        ((IConfigurationBuilder)builder.Configuration).Sources.Clear();
        var config = new Dictionary<string, string?>
        {
            // Only here to satisfy ConfigurationOptions.Parse inside AddRedisMultiplexer - nothing
            // in this test ever resolves IConnectionMultiplexer, so this is never actually
            // connected to.
            ["ConnectionStrings:Redis"] = "localhost:6379",
        };
        if (permitLimit is { } pl) config["RateLimiting:PermitLimit"] = pl.ToString();
        if (windowSeconds is { } ws) config["RateLimiting:WindowSeconds"] = ws.ToString();
        builder.Configuration.AddInMemoryCollection(config);

        builder.AddAuthRateLimiting();

        return builder.Services.BuildServiceProvider().GetRequiredService<RateLimitPolicy>();
    }

    [Fact]
    public void AddAuthRateLimiting_RegistersLocalFallbackPolicy()
    {
        // LocalFallback, not FailOpen (RedirectApi's own choice) or FailClosed (this policy's
        // choice until a live topology QA pass, 2026-10-29, found FailClosed meant register/login
        // simply didn't work in any topology with Redis disabled) - see AddAuthRateLimiting's own
        // comment.
        var policy = BuildPolicy();

        Assert.Equal(RateLimitFailureMode.LocalFallback, policy.FailureMode);
    }

    [Fact]
    public void AddAuthRateLimiting_UsesConfiguredPermitLimitAndWindow()
    {
        var policy = BuildPolicy(permitLimit: 42, windowSeconds: 7);

        Assert.Equal(42, policy.PermitLimit);
        Assert.Equal(TimeSpan.FromSeconds(7), policy.Window);
    }

    [Fact]
    public void AddAuthRateLimiting_MissingConfigSection_FallsBackToRecordDefaults()
    {
        // No RateLimiting:* keys set - must fall back to RateLimitingOptions' own defaults
        // (100/60s) rather than throwing or binding to 0 (which would reject every request
        // immediately, same silent-zero trap BottleneckThresholdsConfigBindingTests covers for the
        // unrelated BottleneckThresholds type).
        var policy = BuildPolicy();

        Assert.Equal(100, policy.PermitLimit);
        Assert.Equal(TimeSpan.FromSeconds(60), policy.Window);
    }

    [Fact]
    public void AddAuthRateLimiting_PartitionKeySelector_KeysByRemoteIpAddress()
    {
        var policy = BuildPolicy();
        var httpContext = new DefaultHttpContext();
        httpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("5.6.7.8");

        Assert.Equal("5.6.7.8", policy.PartitionKeySelector(httpContext));
    }

    [Fact]
    public void AddAuthRateLimiting_PartitionKeySelector_NullRemoteIpAddress_FallsBackToUnknownBucket()
    {
        // RemoteIpAddress is null only for non-socket transports (e.g. in-process test servers) -
        // AuthApi is never reached that way outside tests, where this filter isn't registered at
        // all. This must fail safe (a shared, generous bucket) instead of throwing.
        var policy = BuildPolicy();
        var httpContext = new DefaultHttpContext();

        Assert.Equal("unknown", policy.PartitionKeySelector(httpContext));
    }
}
