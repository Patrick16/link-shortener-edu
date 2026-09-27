using Moq;
using RabbitMQ.Client;

namespace Infrastructure.Tests;

public class RabbitMqClientTests
{
    [Fact]
    public void IsUsable_NullTask_ReturnsFalse()
    {
        Assert.False(RabbitMqClient.IsUsable(null));
    }

    [Fact]
    public void IsUsable_FaultedTask_ReturnsFalse()
    {
        var task = Task.FromException<IConnection>(new InvalidOperationException("connect failed"));
        Assert.False(RabbitMqClient.IsUsable(task));
    }

    [Fact]
    public void IsUsable_CanceledTask_ReturnsFalse()
    {
        var tcs = new TaskCompletionSource<IConnection>();
        tcs.SetCanceled();
        Assert.False(RabbitMqClient.IsUsable(tcs.Task));
    }

    [Fact]
    public void IsUsable_StillConnecting_ReturnsTrue()
    {
        // Not yet completed - a caller should await it rather than treat it as dead.
        var tcs = new TaskCompletionSource<IConnection>();
        Assert.True(RabbitMqClient.IsUsable(tcs.Task));
    }

    [Fact]
    public void IsUsable_CompletedWithOpenConnection_ReturnsTrue()
    {
        var connection = new Mock<IConnection>();
        connection.SetupGet(c => c.IsOpen).Returns(true);

        Assert.True(RabbitMqClient.IsUsable(Task.FromResult(connection.Object)));
    }

    [Fact]
    public void IsUsable_CompletedWithClosedConnection_ReturnsFalse()
    {
        // The regression this guards: once a connection attempt succeeds, the Task sits at
        // RanToCompletion forever even after the broker closes it (a restart, a chaos experiment, a
        // network blip). Checking only Task.Status (Faulted/Canceled) missed this entirely and kept
        // handing out the same dead IConnection to every future caller, forever - IsOpen is the only
        // way to tell the difference between "still good" and "was good, now dead".
        var connection = new Mock<IConnection>();
        connection.SetupGet(c => c.IsOpen).Returns(false);

        Assert.False(RabbitMqClient.IsUsable(Task.FromResult(connection.Object)));
    }
}
