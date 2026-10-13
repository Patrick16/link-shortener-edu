using ReportingApi.Realtime;

namespace ReportingApi.Tests;

public class TopLinksSubscriptionsTests
{
    [Fact]
    public void IsWatched_FalseUntilAConnectionOpens()
    {
        var sut = new TopLinksSubscriptions();

        Assert.False(sut.IsWatched(Guid.NewGuid()));
    }

    [Fact]
    public void Disconnected_OnlyStopsBeingWatchedAfterTheLastTabCloses()
    {
        // Two open tabs for the same user - closing one must not stop the other from receiving pushes.
        var sut = new TopLinksSubscriptions();
        var userId = Guid.NewGuid();
        sut.Connected(userId);
        sut.Connected(userId);

        sut.Disconnected(userId);
        Assert.True(sut.IsWatched(userId));

        sut.Disconnected(userId);
        Assert.False(sut.IsWatched(userId));
    }

    [Fact]
    public void Disconnected_WithoutAMatchingConnect_DoesNotThrow()
    {
        var sut = new TopLinksSubscriptions();

        sut.Disconnected(Guid.NewGuid());
    }
}
