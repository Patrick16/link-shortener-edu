using Common.Models;
using Microsoft.Extensions.Caching.Hybrid;

namespace Common;

// HybridCache-backed counterpart to LinkCacheService - same Key(id) format (link:{id}), so
// whichever backend Cache:Backend selects reads/writes the exact same logical cache entries.
// See HybridEntityCacheService's own comment for what this gives up relative to LinkCacheService.
public class HybridLinkCacheService(HybridCache cache) : HybridEntityCacheService<Link>(cache)
{
    protected override string Key(string id) => $"link:{id}";
}
