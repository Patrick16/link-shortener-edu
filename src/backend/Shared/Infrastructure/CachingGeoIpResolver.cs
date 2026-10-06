using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

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
public sealed class CachingGeoIpResolver(IGeoIpResolver inner, IDistributedCache cache, ILogger<CachingGeoIpResolver> logger) : IGeoIpResolver
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

        // This cache is a pure optimization (sharing one ip-api.com lookup between TrafficService's
        // and ReportingService's independent consumers) - unlike EntityCacheService's identical
        // guard, a Redis blip here used to fail the ENTIRE click.tracked batch item, which in the
        // async/RabbitMQ consumer path only delayed that one message, but in messaging-mode=grpc
        // propagates all the way up through MessagingGrpcServiceBase.Publish and fails the user's
        // redirect itself for a side-effect that has nothing to do with whether the click was
        // actually persisted (found during review - confirmed live: a transient Redis NOREPLICAS
        // error on the cache write turned a successfully-persisted click into a 502 to the browser).
        try
        {
            var cached = await cache.GetStringAsync(key, cancellationToken);
            if (cached is not null)
            {
                return JsonSerializer.Deserialize<GeoLocation>(cached);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read geo-IP cache for {Key} - treating as a cache miss", key);
        }

        var resolved = await inner.ResolveAsync(ipAddress, cancellationToken);

        try
        {
            // Cache every outcome, including a "not found" GeoLocation(null, null) - a failed/empty
            // lookup for a given IP is just as expensive to repeat against the rate limit as a
            // successful one, and just as likely to still be true moments later for the other
            // consumer.
            await cache.SetStringAsync(key, JsonSerializer.Serialize(resolved), CacheOptions, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to populate geo-IP cache for {Key} - continuing without it", key);
        }

        return resolved;
    }
}
