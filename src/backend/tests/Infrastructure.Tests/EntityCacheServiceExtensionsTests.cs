using Microsoft.Extensions.Configuration;

namespace Infrastructure.Tests;

public class EntityCacheServiceExtensionsTests
{
    private static IConfiguration ConfigWith(string? backend)
    {
        var data = new Dictionary<string, string?>();
        if (backend is not null) data["Cache:Backend"] = backend;
        return new ConfigurationBuilder().AddInMemoryCollection(data).Build();
    }

    [Fact]
    public void IsHybridCacheBackend_NotSet_ReturnsFalse()
    {
        Assert.False(ConfigWith(null).IsHybridCacheBackend());
    }

    [Theory]
    [InlineData("legacy")]
    [InlineData("Legacy")]
    public void IsHybridCacheBackend_Legacy_ReturnsFalse(string value)
    {
        Assert.False(ConfigWith(value).IsHybridCacheBackend());
    }

    [Theory]
    [InlineData("hybrid")]
    [InlineData("Hybrid")]
    public void IsHybridCacheBackend_Hybrid_ReturnsTrue(string value)
    {
        Assert.True(ConfigWith(value).IsHybridCacheBackend());
    }

    [Fact]
    public void IsHybridCacheBackend_UnrecognizedValue_ThrowsInsteadOfSilentlyDefaulting()
    {
        var config = ConfigWith("typo");

        Assert.Throws<InvalidOperationException>(() => config.IsHybridCacheBackend());
    }
}
