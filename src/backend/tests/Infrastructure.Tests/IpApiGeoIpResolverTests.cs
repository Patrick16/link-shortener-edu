using System.Net;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.Tests;

public class IpApiGeoIpResolverTests
{
    private sealed class RecordingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(response);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("network down");
    }

    private static HttpResponseMessage SuccessResponse(string country = "US", string city = "Springfield") =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent($$"""{"status":"success","country":"{{country}}","city":"{{city}}"}"""),
        };

    private static IpApiGeoIpResolver NewSut(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://ip-api.test") }, NullLogger<IpApiGeoIpResolver>.Instance);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-ip-address")]
    public async Task ResolveAsync_NullEmptyOrInvalidInput_ReturnsEmptyWithoutCallingApi(string? ip)
    {
        var handler = new RecordingHandler(SuccessResponse());
        var sut = NewSut(handler);

        var result = await sut.ResolveAsync(ip);

        Assert.Equal(new GeoLocation(null, null), result);
        Assert.Equal(0, handler.CallCount);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.1.1")]
    [InlineData("::1")]
    public async Task ResolveAsync_PrivateOrLoopbackAddress_ReturnsEmptyWithoutCallingApi(string ip)
    {
        var handler = new RecordingHandler(SuccessResponse());
        var sut = NewSut(handler);

        var result = await sut.ResolveAsync(ip);

        Assert.Equal(new GeoLocation(null, null), result);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task ResolveAsync_IPv4MappedIPv6PrivateAddress_ReturnsEmptyWithoutCallingApi()
    {
        // Regression: Kestrel's RemoteIpAddress on a dual-stack socket commonly hands back an
        // IPv4-mapped IPv6 address like "::ffff:172.18.0.25" for what is really an IPv4 docker-
        // internal client. Without unmapping first, this was treated as a public address and made
        // a real ip-api.com call for every single click.
        var handler = new RecordingHandler(SuccessResponse());
        var sut = NewSut(handler);

        var result = await sut.ResolveAsync("::ffff:172.18.0.25");

        Assert.Equal(new GeoLocation(null, null), result);
        Assert.Equal(0, handler.CallCount);
    }

    [Theory]
    [InlineData("172.15.255.255")] // just below the 172.16/12 private range
    [InlineData("172.32.0.1")] // just above it
    [InlineData("192.169.1.1")] // not 192.168/16
    public async Task ResolveAsync_PublicLookingAddress_CallsApi(string ip)
    {
        var handler = new RecordingHandler(SuccessResponse());
        var sut = NewSut(handler);

        await sut.ResolveAsync(ip);

        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task ResolveAsync_PublicAddress_SuccessResponse_ReturnsCountryAndCity()
    {
        var handler = new RecordingHandler(SuccessResponse("DE", "Berlin"));
        var sut = NewSut(handler);

        var result = await sut.ResolveAsync("8.8.8.8");

        Assert.Equal(new GeoLocation("DE", "Berlin"), result);
    }

    [Fact]
    public async Task ResolveAsync_NonSuccessStatus_ReturnsEmpty()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"status":"fail"}"""),
        });
        var sut = NewSut(handler);

        var result = await sut.ResolveAsync("8.8.8.8");

        Assert.Equal(new GeoLocation(null, null), result);
    }

    [Fact]
    public async Task ResolveAsync_HttpCallThrows_ReturnsEmptyInsteadOfThrowing()
    {
        var sut = NewSut(new ThrowingHandler());

        var result = await sut.ResolveAsync("8.8.8.8");

        Assert.Equal(new GeoLocation(null, null), result);
    }
}
