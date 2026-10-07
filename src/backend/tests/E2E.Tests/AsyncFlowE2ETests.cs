using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Npgsql;

namespace E2E.Tests;

// Walks the one real user-visible flow that crosses both async RabbitMQ hops this project
// demonstrates (LinkApi -> ShortenerService via LinkCreatedEvent, RedirectApi -> TrafficService via
// ClickTrackedEvent) against a real, if trimmed, copy of the stack - see docker-compose.e2e.yml for
// exactly what's cut and why. This is a teaching example of an e2e test over this architecture, not
// a production regression suite: one flow, one assertion per async hop, no attempt at the coverage
// the fast per-service unit/integration tests already provide.
//
// Requires Docker and the 5 service images already built:
//   docker compose -f sandbox/docker-compose.yml build auth-api link-api redirect-api shortener-service traffic-service
// Run explicitly - this project is deliberately excluded from LinkShortener.sln so
// `dotnet test src/backend/LinkShortener.sln` (the fast, Docker-free suite) never touches it:
//   dotnet test src/backend/tests/E2E.Tests/E2E.Tests.csproj
public sealed class AsyncFlowE2ETests : IAsyncLifetime
{
    // Matches the docker:27-cli tag control-api's own Dockerfile already pins elsewhere in this
    // repo - Compose itself runs inside this helper container, against the mounted Docker socket.
    private const string DockerCliImage = "docker:27-cli";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private ComposeContainer _compose = null!;
    private HttpClient _authApi = null!;
    private HttpClient _linkApi = null!;
    private HttpClient _redirectApi = null!;
    private string _clicksDbConnectionString = null!;

    public async Task InitializeAsync()
    {
        var composeFilePath = Path.Combine(AppContext.BaseDirectory, "docker-compose.e2e.yml");

        // Every service in the compose file already has its own healthcheck (same style as the real
        // sandbox/docker-compose.yml), and depends_on: condition: service_healthy wires them in the
        // right order - `--wait` just makes `docker compose up` itself block until that whole chain
        // is actually healthy, instead of returning the moment containers are merely started.
        _compose = new ComposeBuilder(DockerCliImage)
            .WithComposeFile(composeFilePath)
            .WithExposedService("postgres", 5432)
            .WithExposedService("auth-api", 8080)
            .WithExposedService("link-api", 8080)
            .WithExposedService("redirect-api", 8080)
            .WithComposeUpOption("--wait", "--wait-timeout", "180")
            .Build();

        await _compose.StartAsync();

        _authApi = NewServiceClient("auth-api");
        _linkApi = NewServiceClient("link-api");
        _redirectApi = NewServiceClient("redirect-api");

        var postgresHost = _compose.GetServiceHost("postgres", 5432);
        var postgresPort = _compose.GetServicePort("postgres", 5432);
        _clicksDbConnectionString = $"Host={postgresHost};Port={postgresPort};Username=postgres;Password=postgres;Database=clicks_db";
    }

    private HttpClient NewServiceClient(string serviceName)
    {
        var host = _compose.GetServiceHost(serviceName, 8080);
        var port = _compose.GetServicePort(serviceName, 8080);
        // AllowAutoRedirect defaults to true - without disabling it here, RedirectApi's 302 would
        // be followed transparently (landing on originalLink itself, an external example.com URL
        // in this test), and the poll below would never see the 302 that actually proves the async
        // hop completed.
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        return new HttpClient(handler) { BaseAddress = new UriBuilder(Uri.UriSchemeHttp, host, port).Uri };
    }

    public async Task DisposeAsync()
    {
        _authApi?.Dispose();
        _linkApi?.Dispose();
        _redirectApi?.Dispose();
        await _compose.StopAsync();
    }

    [Fact]
    public async Task RegisterLoginCreateLinkAndClick_PropagatesThroughRabbitMqToPersistence()
    {
        var email = $"e2e-{Guid.NewGuid():N}@example.com";
        const string password = "correct-horse-battery-staple-1";

        var registerResponse = await _authApi.PostAsJsonAsync("/register", new { name = "E2E Test User", email, password });
        registerResponse.EnsureSuccessStatusCode();

        var loginResponse = await _authApi.PostAsJsonAsync("/login", new { email, password });
        loginResponse.EnsureSuccessStatusCode();
        var login = await loginResponse.Content.ReadFromJsonAsync<LoginResponse>(JsonOptions);
        Assert.False(string.IsNullOrEmpty(login?.Token));

        // LinkApi never requires a Bearer token (see LinksController) - sent anyway so this walk
        // matches the real register -> login -> create flow a user would actually follow.
        _linkApi.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.Token);

        var originalLink = $"https://example.com/e2e/{Guid.NewGuid():N}";
        var createResponse = await _linkApi.PostAsJsonAsync("/Links", new { originalLink });
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<CreateLinkResponse>(JsonOptions);
        Assert.False(string.IsNullOrEmpty(created?.ShortenLink));
        var hash = created!.ShortenLink;

        // First async hop: LinkApi responds before ShortenerService has actually consumed the
        // LinkCreatedEvent and persisted the row to links_db - RedirectApi 404s until it catches up
        // (the same eventual-consistency window documented on TrafficService's own
        // redirect-api.resolve endpoint definition). Retrying the redirect is what proves this hop
        // completed.
        HttpResponseMessage? redirectResponse = null;
        for (var attempt = 0; attempt < 30 && redirectResponse?.StatusCode is not (HttpStatusCode.Redirect or HttpStatusCode.Found); attempt++)
        {
            redirectResponse?.Dispose();
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/{hash}");
            redirectResponse = await _redirectApi.SendAsync(request);
            if (redirectResponse.StatusCode is not (HttpStatusCode.Redirect or HttpStatusCode.Found))
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }

        Assert.True(
            redirectResponse?.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found,
            $"Expected link-api -> RabbitMQ -> shortener-service to have persisted '{hash}' to links_db within the poll window, but the last redirect attempt returned {redirectResponse?.StatusCode}");

        // Second async hop: the click above already made RedirectApi publish a ClickTrackedEvent -
        // traffic-service exposes no HTTP API of its own to ask about it, so this polls clicks_db
        // directly for the row traffic-service's consumer is expected to have written by now.
        var clickPersisted = false;
        for (var attempt = 0; attempt < 30 && !clickPersisted; attempt++)
        {
            await using var connection = new NpgsqlConnection(_clicksDbConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT 1 FROM clicks WHERE \"Hash\" = @hash LIMIT 1", connection);
            command.Parameters.AddWithValue("hash", hash);
            clickPersisted = await command.ExecuteScalarAsync() is not null;

            if (!clickPersisted)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }

        Assert.True(clickPersisted, $"Expected redirect-api -> RabbitMQ -> traffic-service to have written a clicks_db row for '{hash}' within the poll window");
    }

    private sealed record LoginResponse(string Token);

    private sealed record CreateLinkResponse(string ShortenLink);
}
