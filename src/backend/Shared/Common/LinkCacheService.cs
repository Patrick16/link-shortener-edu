using Common.Models;
using Microsoft.Extensions.Caching.Distributed;

namespace Common;

// Shared between LinkApi (writes/populates the cache on create/lookup) and RedirectApi (reads it
// on redirect) — living here, not in either service, is what keeps both sides using the exact same
// Redis key format instead of risking silent drift if each had its own copy.
public class LinkCacheService(IDistributedCache cache) :
    EntityCacheService<Link>(cache)
{
    protected override string Key(string id) => $"link:{id}";
}
