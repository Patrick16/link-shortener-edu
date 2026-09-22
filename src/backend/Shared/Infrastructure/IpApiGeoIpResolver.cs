using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Infrastructure;

// Resolves country/city from an IP via ip-api.com's free JSON endpoint (no API key, ~45 req/min
// per caller). Fully best-effort: on a private/loopback address (the common case in local
// docker-compose runs, where the client IP is usually a docker-internal address), a timeout, or any
// other failure, this returns an empty GeoLocation rather than throwing — a click must still get
// recorded even if geolocation isn't available. Swap for a local MaxMind GeoLite2 database instead
// if an offline/rate-limit-free resolver is ever needed.
public sealed class IpApiGeoIpResolver(HttpClient httpClient, ILogger<IpApiGeoIpResolver> logger) : IGeoIpResolver
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly ILogger<IpApiGeoIpResolver> _logger = logger;

    private sealed record IpApiResponse(string Status, string? Country, string? City);

    public async Task<GeoLocation> ResolveAsync(string? ipAddress, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(ipAddress) ||
            !IPAddress.TryParse(ipAddress, out var parsed) ||
            IsPrivateOrLoopback(parsed))
        {
            return new GeoLocation(null, null);
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(3));

            var response = await _httpClient
                .GetFromJsonAsync<IpApiResponse>($"/json/{ipAddress}?fields=status,country,city", cts.Token)
                .ConfigureAwait(false);

            return response is { Status: "success" }
                ? new GeoLocation(response.Country, response.City)
                : new GeoLocation(null, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Geo IP lookup failed for {IpAddress}, storing click without geo data", ipAddress);
            return new GeoLocation(null, null);
        }
    }

    private static bool IsPrivateOrLoopback(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal)
        {
            return true;
        }

        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return false;
        }

        var bytes = ip.GetAddressBytes();
        return bytes[0] switch
        {
            10 => true,
            127 => true,
            172 => bytes[1] is >= 16 and <= 31,
            192 => bytes[1] == 168,
            169 => bytes[1] == 254, // link-local
            _ => false,
        };
    }
}
