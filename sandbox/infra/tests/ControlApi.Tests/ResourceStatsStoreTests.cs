using ControlApi.Models;
using ControlApi.Services;

namespace ControlApi.Tests;

public class ResourceStatsStoreTests
{
    private static ResourceSample Sample(string serviceId, int secondsAgo) =>
        new(serviceId, CpuPercent: 12.5, MemoryUsageBytes: 1024, MemoryLimitBytes: 4096,
            Timestamp: DateTimeOffset.UtcNow.AddSeconds(-secondsAgo));

    [Fact]
    public void GetHistory_UnknownService_ReturnsEmpty()
    {
        var sut = new ResourceStatsStore();

        var history = sut.GetHistory("unknown-service");

        Assert.Empty(history);
    }

    [Fact]
    public void Add_ThenGetHistory_ReturnsAddedSample()
    {
        var sut = new ResourceStatsStore();
        var sample = Sample("link-api", 0);

        sut.Add(sample);

        Assert.Equal([sample], sut.GetHistory("link-api"));
    }

    [Fact]
    public void GetHistory_KeepsServicesSeparate()
    {
        var sut = new ResourceStatsStore();
        var linkSample = Sample("link-api", 0);
        var redirectSample = Sample("redirect-api", 0);

        sut.Add(linkSample);
        sut.Add(redirectSample);

        Assert.Equal([linkSample], sut.GetHistory("link-api"));
        Assert.Equal([redirectSample], sut.GetHistory("redirect-api"));
    }

    [Fact]
    public void Add_MoreThanThirtySamples_DropsOldestFirst()
    {
        var sut = new ResourceStatsStore();
        var samples = Enumerable.Range(0, 35).Select(i => Sample("link-api", secondsAgo: 35 - i)).ToList();
        foreach (var sample in samples)
        {
            sut.Add(sample);
        }

        var history = sut.GetHistory("link-api");

        Assert.Equal(30, history.Count);
        Assert.Equal(samples.Skip(5), history);
    }

    [Fact]
    public void Add_PreservesInsertionOrder()
    {
        var sut = new ResourceStatsStore();
        var first = Sample("link-api", 2);
        var second = Sample("link-api", 1);

        sut.Add(first);
        sut.Add(second);

        var history = sut.GetHistory("link-api");
        Assert.Equal([first, second], history);
    }
}
