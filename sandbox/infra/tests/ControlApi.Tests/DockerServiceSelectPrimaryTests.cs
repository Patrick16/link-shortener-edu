using ControlApi.Services;
using Docker.DotNet.Models;

namespace ControlApi.Tests;

public class DockerServiceSelectPrimaryTests
{
    private static ContainerListResponse WithContainerNumber(string id, string? containerNumber)
    {
        var labels = new Dictionary<string, string>();
        if (containerNumber is not null)
        {
            labels["com.docker.compose.container-number"] = containerNumber;
        }

        return new ContainerListResponse { ID = id, Labels = labels };
    }

    [Fact]
    public void SelectPrimary_MultipleReplicas_AlwaysPicksLowestContainerNumberRegardlessOfListOrder()
    {
        // This is the actual regression: Docker's ListContainersAsync order isn't documented/stable,
        // so picking containers[0] used to mean repeated Stop/Start/Restart/Degrade calls against a
        // scaled service could silently act on a *different* physical container each time, and
        // diverge from what the frontend shows (always the lowest containerNumber). Ordering
        // explicitly makes the choice deterministic and matches that same convention.
        var two = WithContainerNumber("c-two", "2");
        var one = WithContainerNumber("c-one", "1");
        var three = WithContainerNumber("c-three", "3");

        Assert.Equal("c-one", DockerService.SelectPrimary([two, one, three])?.ID);
        Assert.Equal("c-one", DockerService.SelectPrimary([one, two, three])?.ID);
        Assert.Equal("c-one", DockerService.SelectPrimary([three, two, one])?.ID);
    }

    [Fact]
    public void SelectPrimary_SingleContainer_ReturnsItRegardlessOfMissingLabel()
    {
        // An unscaled service still has exactly one container, whether or not Compose happened to
        // set the container-number label on it - this must keep working exactly as before.
        var only = WithContainerNumber("c-only", containerNumber: null);

        Assert.Equal("c-only", DockerService.SelectPrimary([only])?.ID);
    }

    [Fact]
    public void SelectPrimary_NoContainers_ReturnsNull()
    {
        Assert.Null(DockerService.SelectPrimary([]));
    }
}
