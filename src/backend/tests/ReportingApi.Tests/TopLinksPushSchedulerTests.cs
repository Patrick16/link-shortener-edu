using ReportingApi.Realtime;

namespace ReportingApi.Tests;

public class TopLinksPushSchedulerTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    private static readonly DateTime Start = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TakeDue_FirstChangeForAUser_IsDueImmediately()
    {
        var sut = new TopLinksPushScheduler();
        var userId = Guid.NewGuid();
        sut.MarkDirty(userId);

        var due = sut.TakeDue(Start, Interval);

        Assert.Equal([userId], due);
    }

    [Fact]
    public void TakeDue_ChangeInsideTheInterval_WaitsForTheNextWindow()
    {
        var sut = new TopLinksPushScheduler();
        var userId = Guid.NewGuid();
        sut.MarkDirty(userId);
        sut.TakeDue(Start, Interval);

        sut.MarkDirty(userId);

        Assert.Empty(sut.TakeDue(Start.AddSeconds(1), Interval));
        Assert.Equal([userId], sut.TakeDue(Start.AddSeconds(2), Interval));
    }

    [Fact]
    public void TakeDue_BurstOfChanges_ProducesOnePushNotOnePerChange()
    {
        var sut = new TopLinksPushScheduler();
        var userId = Guid.NewGuid();
        for (var i = 0; i < 100; i++)
        {
            sut.MarkDirty(userId);
        }

        var due = sut.TakeDue(Start, Interval);

        Assert.Single(due);
        Assert.Empty(sut.TakeDue(Start, Interval));
    }

    [Fact]
    public void TakeDue_OnlyDirtyUsersAreReturned()
    {
        var sut = new TopLinksPushScheduler();
        var changed = Guid.NewGuid();
        var untouched = Guid.NewGuid();
        sut.MarkDirty(changed);

        var due = sut.TakeDue(Start, Interval);

        Assert.DoesNotContain(untouched, due);
        Assert.Contains(changed, due);
    }

    [Fact]
    public void Forget_ClearsLastPayload_SoAReconnectDoesNotSkipItsFirstPush()
    {
        var sut = new TopLinksPushScheduler();
        var userId = Guid.NewGuid();
        sut.RecordPayload(userId, "[]");

        sut.Forget(userId);

        Assert.False(sut.IsUnchanged(userId, "[]"));
    }

    [Fact]
    public void IsUnchanged_SamePayloadAsLastRecorded_ReturnsTrue()
    {
        var sut = new TopLinksPushScheduler();
        var userId = Guid.NewGuid();
        sut.RecordPayload(userId, "[1]");

        Assert.True(sut.IsUnchanged(userId, "[1]"));
        Assert.False(sut.IsUnchanged(userId, "[2]"));
    }

    [Fact]
    public void Forget_ClearsThrottleState_SoAReconnectIsPushedImmediately()
    {
        var sut = new TopLinksPushScheduler();
        var userId = Guid.NewGuid();
        sut.MarkDirty(userId);
        sut.TakeDue(Start, Interval);
        sut.Forget(userId);

        sut.MarkDirty(userId);

        Assert.Equal([userId], sut.TakeDue(Start.AddMilliseconds(100), Interval));
    }
}
