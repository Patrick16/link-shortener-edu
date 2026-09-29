using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;

namespace Infrastructure.Tests;

public class RabbitMqWarmupServiceTests
{
    private static Mock<IChannel> NewOpenChannelMock()
    {
        var channel = new Mock<IChannel>();
        channel.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);
        return channel;
    }

    [Fact]
    public async Task StartAsync_Success_CompletesAndDisposesTheWarmupChannel()
    {
        var channel = NewOpenChannelMock();
        var connection = new Mock<IRabbitMqConnection>();
        connection.Setup(c => c.CreateChannelAsync(It.IsAny<CancellationToken>())).ReturnsAsync(channel.Object);
        var sut = new RabbitMqWarmupService(connection.Object, NullLogger<RabbitMqWarmupService>.Instance);

        await sut.StartAsync(CancellationToken.None);

        channel.Verify(c => c.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task StartAsync_ConnectionThrows_DoesNotFailHostStartup()
    {
        var connection = new Mock<IRabbitMqConnection>();
        connection.Setup(c => c.CreateChannelAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("broker unreachable"));
        var sut = new RabbitMqWarmupService(connection.Object, NullLogger<RabbitMqWarmupService>.Instance);

        // Must not throw - a broker that's down at boot must never prevent the host from starting.
        await sut.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_ConnectionAttemptNeverCompletes_ReturnsWithinTimeoutBoundInstead()
    {
        // F1 regression: without the Task.WhenAny race, a broker that's merely *slow* (rather than
        // immediately failing) used to leave StartAsync - and therefore the whole container's
        // startup, since Kestrel doesn't accept connections until every IHostedService.StartAsync
        // returns - hanging for as long as the connection attempt took (13-67s observed elsewhere in
        // this investigation, and HostOptions.StartupTimeout is unbounded by default). A
        // never-completing TaskCompletionSource simulates the worst case: no exception, no result,
        // just silence.
        var neverCompletes = new TaskCompletionSource<IChannel>();
        var connection = new Mock<IRabbitMqConnection>();
        connection.Setup(c => c.CreateChannelAsync(It.IsAny<CancellationToken>())).Returns(neverCompletes.Task);
        var sut = new RabbitMqWarmupService(connection.Object, NullLogger<RabbitMqWarmupService>.Instance);

        var startTask = sut.StartAsync(CancellationToken.None);
        var completed = await Task.WhenAny(startTask, Task.Delay(TimeSpan.FromSeconds(8)));

        Assert.Same(startTask, completed);
    }
}
