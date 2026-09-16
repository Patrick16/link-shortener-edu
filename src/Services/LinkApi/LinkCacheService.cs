using Common;
using Common.Models;
using Microsoft.Extensions.Caching.Distributed;

namespace LinkApi;

public class LinkCacheService(IDistributedCache cache) : 
    EntityCacheService<Link>(cache)
{
    protected override string Key(string id) => $"link:{id}";
}
