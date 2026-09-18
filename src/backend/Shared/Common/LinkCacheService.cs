using Common.Models;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Common;

// Shared between LinkApi (writes/populates the cache on create/lookup) and RedirectApi (reads it
// on redirect) — living here, not in either service, is what keeps both sides using the exact same
// Redis key format instead of risking silent drift if each had its own copy.
public class LinkCacheService(IDistributedCache cache, IConfiguration configuration, ILogger<EntityCacheService<Link>> logger) :
    EntityCacheService<Link>(cache, configuration, logger)
{
    protected override string Key(string id) => $"link:{id}";
}
