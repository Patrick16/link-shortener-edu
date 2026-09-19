using ControlApi.Models;
using ControlApi.Services;

namespace ControlApi.Tests;

public class StatusPollerServiceTests
{
    private static ManagedContainer Container(string serviceId, string state, string status = "Up 3 seconds", int number = 1) =>
        new(serviceId, ContainerId: $"{serviceId}-container", State: state, Status: status, ContainerNumber: number);

    [Fact]
    public void HasChanged_BothEmpty_ReturnsFalse()
    {
        Assert.False(StatusPollerService.HasChanged([], []));
    }

    [Fact]
    public void HasChanged_DifferentCounts_ReturnsTrue()
    {
        var previous = new[] { Container("link-api", "running") };
        var current = new[] { Container("link-api", "running"), Container("redirect-api", "running") };

        Assert.True(StatusPollerService.HasChanged(previous, current));
    }

    [Fact]
    public void HasChanged_SameStateDifferentStatusText_ReturnsFalse()
    {
        // Docker's Status string embeds an elapsed-time clock ("Up 3 seconds" -> "Up 4 seconds")
        // that ticks on every poll - only a State transition should count as a change.
        var previous = new[] { Container("link-api", "running", status: "Up 3 seconds") };
        var current = new[] { Container("link-api", "running", status: "Up 4 seconds") };

        Assert.False(StatusPollerService.HasChanged(previous, current));
    }

    [Fact]
    public void HasChanged_StateTransition_ReturnsTrue()
    {
        var previous = new[] { Container("link-api", "running") };
        var current = new[] { Container("link-api", "exited") };

        Assert.True(StatusPollerService.HasChanged(previous, current));
    }

    [Fact]
    public void HasChanged_NewServiceReplacesKnownOne_ReturnsTrue()
    {
        var previous = new[] { Container("link-api", "running") };
        var current = new[] { Container("redirect-api", "running") };

        Assert.True(StatusPollerService.HasChanged(previous, current));
    }

    [Fact]
    public void HasChanged_IdenticalLists_ReturnsFalse()
    {
        var previous = new[] { Container("link-api", "running"), Container("redirect-api", "running") };
        var current = new[] { Container("link-api", "running"), Container("redirect-api", "running") };

        Assert.False(StatusPollerService.HasChanged(previous, current));
    }
}
