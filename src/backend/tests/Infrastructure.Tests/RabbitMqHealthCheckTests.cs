using Microsoft.Extensions.Diagnostics.HealthChecks;
using Moq;
using RabbitMQ.Client;

namespace Infrastructure.Tests;

public class RabbitMqHealthCheckTests
{
    private static Mock<IChannel> NewOpenChannelMock()
    {
        var channel = new Mock<IChannel>();
        channel.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);
        return channel;
    }

    [Fact]
    public async Task CheckHealthAsync_ConnectionAlreadyOpen_ReturnsHealthyWithoutOpeningAChannel()
    {
        // Regression: this check used to open (and immediately dispose) a brand-new channel on every
        // single probe, even when the shared connection was already known to be open - under a
        // connection-establishment burst elsewhere on that same shared connection, that round trip
        // consistently landed on this check's own 3s Timeout instead of actually completing fast
        // (/health/ready p95 tracked the Timeout almost exactly under load). IsOpen must short-circuit
        // that entirely.
        var connection = new Mock<IRabbitMqConnection>();
        connection.SetupGet(c => c.IsOpen).Returns(true);
        var sut = new RabbitMqHealthCheck(connection.Object);

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        connection.Verify(c => c.CreateChannelAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CheckHealthAsync_ConnectionNotOpen_FallsBackToOpeningAChannel()
    {
        var connection = new Mock<IRabbitMqConnection>();
        connection.SetupGet(c => c.IsOpen).Returns(false);
        connection.Setup(c => c.CreateChannelAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(NewOpenChannelMock().Object);
        var sut = new RabbitMqHealthCheck(connection.Object);

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Healthy, result.Status);
        connection.Verify(c => c.CreateChannelAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CheckHealthAsync_ConnectionNotOpenAndChannelCreationThrows_ReturnsUnhealthy()
    {
        var connection = new Mock<IRabbitMqConnection>();
        connection.SetupGet(c => c.IsOpen).Returns(false);
        connection.Setup(c => c.CreateChannelAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("broker unreachable"));
        var sut = new RabbitMqHealthCheck(connection.Object);

        var result = await sut.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }
}
