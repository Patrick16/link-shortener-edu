using ControlApi.Models;
using ControlApi.Services;

namespace ControlApi.Tests;

public class RunResourceMaxTrackerTests
{
    private static ResourceSample Sample(string serviceId, double cpuPercent, long memoryUsageBytes, long memoryLimitBytes) =>
        new(serviceId, ContainerId: $"{serviceId}-1", ContainerNumber: 1, cpuPercent, memoryUsageBytes, memoryLimitBytes, TcpConnections: 0, DateTimeOffset.UtcNow);

    [Fact]
    public void Observe_WithoutBeginRun_IsIgnored()
    {
        var sut = new RunResourceMaxTracker();

        sut.Observe([Sample("link-api", 50, 100, 1000)]);
        var result = sut.EndRun();

        Assert.Empty(result);
    }

    [Fact]
    public void Observe_AfterBeginRun_TracksMaxPerService()
    {
        var sut = new RunResourceMaxTracker();
        sut.BeginRun();

        sut.Observe([Sample("link-api", 30, 100, 1000)]);
        sut.Observe([Sample("link-api", 70, 300, 1000)]);
        sut.Observe([Sample("link-api", 20, 50, 1000)]);
        var result = sut.EndRun();

        var node = Assert.Single(result);
        Assert.Equal("link-api", node.ServiceId);
        Assert.Equal(70, node.MaxCpuPercent);
        Assert.Equal(300, node.MaxMemoryUsageBytes);
    }

    [Fact]
    public void Observe_ComputesMemoryPercentAgainstEachSamplesOwnLimit()
    {
        var sut = new RunResourceMaxTracker();
        sut.BeginRun();

        sut.Observe([Sample("postgres", cpuPercent: 10, memoryUsageBytes: 500, memoryLimitBytes: 1000)]);
        var result = sut.EndRun();

        var node = Assert.Single(result);
        Assert.Equal(50, node.MaxMemoryPercent);
    }

    [Fact]
    public void Observe_ZeroMemoryLimit_DoesNotThrowAndReportsZeroPercent()
    {
        var sut = new RunResourceMaxTracker();
        sut.BeginRun();

        sut.Observe([Sample("redis-master", cpuPercent: 5, memoryUsageBytes: 500, memoryLimitBytes: 0)]);
        var result = sut.EndRun();

        var node = Assert.Single(result);
        Assert.Equal(0, node.MaxMemoryPercent);
    }

    [Fact]
    public void Observe_MultipleServices_TracksEachIndependently()
    {
        var sut = new RunResourceMaxTracker();
        sut.BeginRun();

        sut.Observe([
            Sample("link-api", 80, 100, 1000),
            Sample("pgcat", 20, 200, 1000),
        ]);
        var result = sut.EndRun();

        Assert.Equal(2, result.Count);
        Assert.Equal(80, result.Single(n => n.ServiceId == "link-api").MaxCpuPercent);
        Assert.Equal(20, result.Single(n => n.ServiceId == "pgcat").MaxCpuPercent);
    }

    [Fact]
    public void EndRun_ClearsStateForNextRun()
    {
        var sut = new RunResourceMaxTracker();
        sut.BeginRun();
        sut.Observe([Sample("link-api", 90, 900, 1000)]);
        sut.EndRun();

        sut.BeginRun();
        sut.Observe([Sample("link-api", 10, 100, 1000)]);
        var secondResult = sut.EndRun();

        var node = Assert.Single(secondResult);
        Assert.Equal(10, node.MaxCpuPercent);
    }

    [Fact]
    public void EndRun_StopsTrackingFurtherObservations()
    {
        var sut = new RunResourceMaxTracker();
        sut.BeginRun();
        sut.Observe([Sample("link-api", 50, 500, 1000)]);
        sut.EndRun();

        // Simulates a stray poll tick landing between EndRun() and the next BeginRun().
        sut.Observe([Sample("link-api", 99, 990, 1000)]);
        sut.BeginRun();
        var result = sut.EndRun();

        Assert.Empty(result);
    }
}
