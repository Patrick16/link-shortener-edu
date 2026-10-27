using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RedirectApi;
using WebDefaults;

namespace RedirectApi.Tests;

// AddRedirectRateLimiting wires the shared RedisRateLimitFilter (WebDefaults) - see that class's
// own comment for why it's shared rather than duplicated per app. What's actually
// RedirectApi-specific is just the RateLimitPolicy it builds here (IP-only partition key,
// numbers, FailOpen) - the Redis-check-and-reject mechanics themselves are tested once,
// generically, in Integration.Tests/RedisRateLimitFilterTests.cs and
// Infrastructure.Tests/RedisRateLimiterTests.cs.
public class RedirectRateLimitingTests
{
    private static RateLimitPolicy BuildPolicy(int? permitLimit = null, int? windowSeconds = null)
    {
        var builder = WebApplication.CreateBuilder();
        // Clear what WebApplication.CreateBuilder() auto-loaded (RedirectApi's own real
        // appsettings.json, env vars, ...) - this test needs full control over exactly what's
        // configured, not whatever happens to be sitting in this project's own output directory.
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

        builder.AddRedirectRateLimiting();

        return builder.Services.BuildServiceProvider().GetRequiredService<RateLimitPolicy>();
    }

    [Fact]
    public void AddRedirectRateLimiting_RegistersFailOpenPolicy()
    {
        // FailOpen, not FailClosed (AuthApi's own choice) - Redis is already on this path's
        // critical dependency list (the link cache, the deletion-marker lookup), and this
        // project's established rule for RedirectApi specifically is that a Redis outage degrades
        // gracefully rather than blocking the hot path (see the request-budget feature's own
        // design) - adding a third reason for Redis to block redirects would contradict that.
        var policy = BuildPolicy();

        Assert.Equal(RateLimitFailureMode.FailOpen, policy.FailureMode);
    }

    [Fact]
    public void AddRedirectRateLimiting_UsesConfiguredPermitLimitAndWindow()
    {
        var policy = BuildPolicy(permitLimit: 42, windowSeconds: 7);

        Assert.Equal(42, policy.PermitLimit);
        Assert.Equal(TimeSpan.FromSeconds(7), policy.Window);
    }

    [Fact]
    public void AddRedirectRateLimiting_MissingConfigSection_FallsBackToRecordDefaults()
    {
        var policy = BuildPolicy();

        Assert.Equal(100, policy.PermitLimit);
        Assert.Equal(TimeSpan.FromSeconds(60), policy.Window);
    }

    [Fact]
    public void AddRedirectRateLimiting_PartitionKeySelector_KeysByRemoteIpAddressOnly()
    {
        // Not userId-or-IP, unlike the plan's own original draft wording - RedirectToOrigin never
        // authenticates its caller at all (a short-link click is an anonymous browser navigation;
        // the userId attached to a tracked click comes from the LINK's own owner, not the clicking
        // caller), so there is no caller userId to key on here (found while implementing -
        // corrected from the plan's draft wording, see RedirectApiServiceExtensions' own comment).
        var policy = BuildPolicy();
        var httpContext = new DefaultHttpContext();
        httpContext.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("5.6.7.8");

        Assert.Equal("5.6.7.8", policy.PartitionKeySelector(httpContext));
    }

    [Fact]
    public void AddRedirectRateLimiting_PartitionKeySelector_NullRemoteIpAddress_FallsBackToUnknownBucket()
    {
        var policy = BuildPolicy();
        var httpContext = new DefaultHttpContext();

        Assert.Equal("unknown", policy.PartitionKeySelector(httpContext));
    }
}
