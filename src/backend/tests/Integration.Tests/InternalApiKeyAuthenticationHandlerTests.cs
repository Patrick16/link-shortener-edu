using System.Net;
using Common;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WebDefaults;

namespace Integration.Tests;

public class InternalApiKeyAuthenticationHandlerTests
{
    private const string ConfiguredKey = "test-internal-key";

    // Mirrors how LinkApi's Program.cs registers the scheme and how LinksController.GetLinks
    // combines it with JwtBearer - a minimal host with just this scheme is enough to exercise the
    // handler itself without needing a real JWT issuer or a DbContext.
    private static async Task<TestServer> NewServerAsync()
    {
        var builder = new HostBuilder().ConfigureWebHost(webHost =>
        {
            webHost.UseTestServer();
            webHost.ConfigureAppConfiguration(config =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:ApiKey"] = ConfiguredKey });
            });
            webHost.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddAuthentication(Constants.InternalApiKeyAuthenticationScheme)
                    .AddScheme<AuthenticationSchemeOptions, InternalApiKeyAuthenticationHandler>(
                        Constants.InternalApiKeyAuthenticationScheme, null);
                services.AddAuthorization();
            });
            webHost.Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapGet("/secure", (HttpContext ctx) =>
                            Results.Ok(new { isInternal = ctx.User.HasClaim(Constants.InternalClaim, "true") }))
                        .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = Constants.InternalApiKeyAuthenticationScheme });
                });
            });
        });

        var host = await builder.StartAsync();
        return host.GetTestServer();
    }

    [Fact]
    public async Task NoHeader_ReturnsUnauthorized()
    {
        using var server = await NewServerAsync();
        using var client = server.CreateClient();

        var response = await client.GetAsync("/secure");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task WrongKey_ReturnsUnauthorized()
    {
        using var server = await NewServerAsync();
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add(Constants.InternalApiKeyHeaderName, "not-the-right-key");

        var response = await client.GetAsync("/secure");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CorrectKey_AuthenticatesWithInternalClaim()
    {
        using var server = await NewServerAsync();
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Add(Constants.InternalApiKeyHeaderName, ConfiguredKey);

        var response = await client.GetAsync("/secure");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"isInternal\":true", await response.Content.ReadAsStringAsync());
    }
}
