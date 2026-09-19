using Common.Models;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Common.Tests;

public class LinkCacheServiceTests
{
    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();

    [Fact]
    public async Task GetCachedAsync_UsesLinkPrefixedKey()
    {
        // LinkApi and RedirectApi must agree on this key format, or a value cached by one is
        // invisible to the other.
        var cache = new Mock<IDistributedCache>();
        cache.Setup(x => x.GetAsync("link:abc123", It.IsAny<CancellationToken>())).ReturnsAsync((byte[]?)null);
        var sut = new LinkCacheService(cache.Object, EmptyConfig(), NullLogger<EntityCacheService<Link>>.Instance);

        await sut.GetCachedAsync("abc123", CancellationToken.None);

        cache.Verify(x => x.GetAsync("link:abc123", It.IsAny<CancellationToken>()), Times.Once);
    }
}
