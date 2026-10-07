using System.Net;
using System.Net.Http.Json;
using AuthApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AuthApi.Tests;

// Regression test for a bug caught during review of item 12 (rate limiting): the suggested snippet
// used AddFixedWindowLimiter(policyName, ...), which builds ONE shared limiter for every caller
// combined, not a separate bucket per client. Both shapes compile - AddAuthRateLimiting's AddPolicy +
// RateLimitPartition shape is the one that actually keys a separate window per client IP, verified
// live (two simulated RemoteIpAddress values) before AddAuthRateLimiting was written. This drives it
// through a real TestServer pipeline, not just a unit-level call, since partitioning only exists in
// how the rate limiter middleware processes requests - nothing about it shows up from calling
// AddAuthRateLimiting alone.
public class AuthRateLimitingTests
{
    private static async Task<TestServer> NewServerAsync(int permitLimit, int windowSeconds = 60)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RateLimiting:PermitLimit"] = permitLimit.ToString(),
            ["RateLimiting:WindowSeconds"] = windowSeconds.ToString(),
        });
        // AddAuthRateLimiting's OnRejected writes through IProblemDetailsService (see its own
        // comment) - without this, a rejection throws InvalidOperationException instead of
        // responding 429, which AddApiExceptionHandling() registers for real in AuthApi's own
        // Program.cs; this is the minimal piece of that this test actually depends on.
        builder.Services.AddProblemDetails();
        builder.AddAuthRateLimiting();

        var app = builder.Build();

        // Test-only: a TestServer request has no real socket, so RemoteIpAddress is null unless
        // something sets it. Standing in for what Kestrel would populate from the real connection.
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers.TryGetValue("X-Test-Ip", out var ip))
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(ip!);
            }

            await next();
        });

        app.UseRateLimiter();
        app.MapGet("/probe", () => Results.Ok()).RequireRateLimiting(AuthApiServiceExtensions.AuthRateLimitPolicy);

        await app.StartAsync();
        return app.GetTestServer();
    }

    private static async Task<HttpStatusCode> ProbeAsync(HttpClient client, string ip)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/probe");
        request.Headers.Add("X-Test-Ip", ip);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    [Fact]
    public async Task DifferentClientIps_EachGetTheirOwnQuota()
    {
        using var server = await NewServerAsync(permitLimit: 2);
        using var client = server.CreateClient();

        // Each IP's own two requests succeed regardless of what the other IP has already used - if
        // this were the broken shared-limiter shape, "2.2.2.2"'s second call below would already be
        // the third request against one shared quota of 2, and would fail.
        Assert.Equal(HttpStatusCode.OK, await ProbeAsync(client, "1.1.1.1"));
        Assert.Equal(HttpStatusCode.OK, await ProbeAsync(client, "2.2.2.2"));
        Assert.Equal(HttpStatusCode.OK, await ProbeAsync(client, "1.1.1.1"));
        Assert.Equal(HttpStatusCode.OK, await ProbeAsync(client, "2.2.2.2"));

        // Each IP's own third request trips its own limit, not the other's.
        Assert.Equal(HttpStatusCode.TooManyRequests, await ProbeAsync(client, "1.1.1.1"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await ProbeAsync(client, "2.2.2.2"));
    }

    [Fact]
    public async Task SameClientIp_ExceedingPermitLimit_Returns429()
    {
        using var server = await NewServerAsync(permitLimit: 1);
        using var client = server.CreateClient();

        Assert.Equal(HttpStatusCode.OK, await ProbeAsync(client, "9.9.9.9"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await ProbeAsync(client, "9.9.9.9"));
    }

    [Fact]
    public async Task Rejection_ReturnsFullProblemDetailsShape()
    {
        // Regression: the rate limiter's own default rejection handling (no OnRejected set) writes
        // a ProblemDetails body with only {status, traceId} - no Title/Type - once AddProblemDetails()
        // is registered, unlike every other error path in this API (e.g. a bare 404 gets Title
        // "Not Found" filled in automatically). Found by live-testing a real rejection against the
        // running container. AddAuthRateLimiting's explicit OnRejected exists specifically to fix this.
        using var server = await NewServerAsync(permitLimit: 1);
        using var client = server.CreateClient();

        using (var first = await ProbeRawAsync(client, "7.7.7.7"))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        using var rejected = await ProbeRawAsync(client, "7.7.7.7");

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        var problem = await rejected.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal(429, problem!.Status);
        Assert.False(string.IsNullOrEmpty(problem.Title));
        Assert.False(string.IsNullOrEmpty(problem.Type));
    }

    private static async Task<HttpResponseMessage> ProbeRawAsync(HttpClient client, string ip)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/probe");
        request.Headers.Add("X-Test-Ip", ip);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task MissingConfigSection_FallsBackToRecordDefaults()
    {
        // No RateLimiting:* keys set - AddAuthRateLimiting must fall back to RateLimitingOptions'
        // own defaults (100/60s) rather than throwing or binding to 0 (which would reject every
        // request immediately, same silent-zero trap BottleneckThresholdsConfigBindingTests covers
        // for the unrelated BottleneckThresholds type).
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddProblemDetails();
        builder.AddAuthRateLimiting();

        var app = builder.Build();
        app.UseRateLimiter();
        app.MapGet("/probe", () => Results.Ok()).RequireRateLimiting(AuthApiServiceExtensions.AuthRateLimitPolicy);

        await app.StartAsync();
        using var server = app.GetTestServer();
        using var client = server.CreateClient();

        var response = await client.GetAsync("/probe");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
