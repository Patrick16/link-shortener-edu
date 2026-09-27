using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WebDefaults;

namespace Integration.Tests;

public class ApiExceptionHandlingTests
{
    // Every one of AuthApi/LinkApi/RedirectApi's Program.cs wires AddApiExceptionHandling() +
    // UseApiExceptionHandling() the same way - a minimal host with the same two calls and one
    // NotFound()-returning endpoint reproduces the exact pipeline behavior without needing a real
    // service's DbContext/dependencies.
    private static async Task<TestServer> NewServerAsync()
    {
        var builder = new HostBuilder().ConfigureWebHost(webHost =>
        {
            webHost.UseTestServer();
            webHost.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddApiExceptionHandling();
            });
            webHost.Configure(app =>
            {
                app.UseApiExceptionHandling();
                app.UseRouting();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapGet("/missing", () => Results.NotFound());
                });
            });
        });

        var host = await builder.StartAsync();
        return host.GetTestServer();
    }

    [Fact]
    public async Task BareNotFoundResult_IsFormattedAsProblemDetailsJson()
    {
        // Regression: AddProblemDetails() alone only formats a response that already calls
        // IProblemDetailsService (a caught exception, or an explicit Problem()/ValidationProblem());
        // a bare `Results.NotFound()` is an empty-body 404 unless UseStatusCodePages() is also wired
        // up, and its own default writer produces plain text ("Status Code: 404; Not Found"), not
        // ProblemDetails - both of which architecture.md/01-minimal.md document every error path as
        // uniformly returning.
        using var server = await NewServerAsync();
        using var client = server.CreateClient();

        var response = await client.GetAsync("/missing");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Equal(404, problem!.Status);
    }
}
