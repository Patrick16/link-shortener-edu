using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;

namespace Infrastructure;

// Wraps another IGeoIpResolver with a short-lived Redis cache keyed by IP address.
//
// Exists because two independent fan-out consumers of the same ClickTrackedEvent
// (TrafficService and ReportingService) each resolve geo-IP for the same click against
// ip-api.com's shared ~45 req/min free tier - without this, adding ReportingService as a second
// consumer roughly doubled the request rate against that one shared budget, risking throttled/
// null geo data in BOTH services under real traffic (found during review, not guessed). Caller
// registrations in TrafficServiceExtensions/ReportingServiceExtensions both point this at the
// same Redis instance *and* the same Redis:InstanceName, so a cache entry either consumer writes
// is visible to the other - whichever of the two near-simultaneous deliveries resolves first wins
// the real API call, the other has a real chance of hitting cache instead.
public sealed class CachingGeoIpResolver(IGeoIpResolver inner, IDistributedCache cache) : IGeoIpResolver
{
    // Comfortably longer than how far apart two fan-out consumers' deliveries for the same event
    // can realistically drift, and short enough that a visitor's geolocation (which can genuinely
    // change between sessions, e.g. mobile networks) doesn't go stale for long.
    private static readonly DistributedCacheEntryOptions CacheOptions =
        new() { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10) };

    public async Task<GeoLocation> ResolveAsync(string? ipAddress, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(ipAddress))
        {
            return await inner.ResolveAsync(ipAddress, cancellationToken);
        }

        var key = $"geoip:{ipAddress}";
        var cached = await cache.GetStringAsync(key, cancellationToken);
        if (cached is not null)
        {
            return JsonSerializer.Deserialize<GeoLocation>(cached);
        }

        var resolved = await inner.ResolveAsync(ipAddress, cancellationToken);
        // Cache every outcome, including a "not found" GeoLocation(null, null) - a failed/empty
        // lookup for a given IP is just as expensive to repeat against the rate limit as a
        // successful one, and just as likely to still be true moments later for the other consumer.
        await cache.SetStringAsync(key, JsonSerializer.Serialize(resolved), CacheOptions, cancellationToken);
        return resolved;
    }
}
