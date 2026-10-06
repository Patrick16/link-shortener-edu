using Common.Models;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Common;

// Shared between LinkApi (writes/populates the cache on create/lookup), RedirectApi (reads it on
// redirect) and ShortenerService (pre-warms it after persisting) — living here, not in each
// service, keeps the $"link:{id}" format itself from drifting. That alone isn't sufficient for a
// shared cache, though: IDistributedCache's Redis implementation prepends each service's own
// Redis:InstanceName ahead of this key (see AddRedisDistributedCache), so three services with three
// different InstanceName values - which is exactly what LinkApi/RedirectApi/ShortenerService's own
// appsettings.json used to set - still end up writing to three different physical Redis keys
// despite running identical Key(id) logic. Found live (create-link, immediate-redirect still
// 404ing after the read-your-writes fix below was already in place) - see
// pitfalls/redis-instance-name-breaks-cross-service-cache-sharing.md. All three now share one
// empty InstanceName for this reason; do not give any of them a distinct one without re-reading
// that pitfall first.
public class LinkCacheService(IDistributedCache cache, IConfiguration configuration, ILogger<EntityCacheService<Link>> logger) :
    EntityCacheService<Link>(cache, configuration, logger)
{
    protected override string Key(string id) => $"link:{id}";
}
