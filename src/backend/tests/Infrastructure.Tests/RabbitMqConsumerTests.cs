using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;

namespace Infrastructure.Tests;

public class RabbitMqConsumerTests
{
    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();

    private static Mock<IChannel> NewWorkingChannelMock()
    {
        var channel = new Mock<IChannel>();
        channel.Setup(c => c.ExchangeDeclareAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<IDictionary<string, object?>?>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        channel.Setup(c => c.QueueDeclareAsync(
                It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<IDictionary<string, object?>?>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QueueDeclareOk("queue", 0, 0));
        channel.Setup(c => c.QueueBindAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IDictionary<string, object?>?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        channel.Setup(c => c.BasicQosAsync(
                It.IsAny<uint>(), It.IsAny<ushort>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        return channel;
    }

    [Fact]
    public async Task SetUpChannelWithRetryAsync_FirstAttemptFails_DisposesFailedChannelBeforeRetrying()
    {
        // Regression: a channel that opened successfully but then failed a later declare/bind/QoS
        // call used to never get disposed before the retry loop opened a replacement - every retry
        // round (up to every 30s during a sustained broker outage) leaked another open channel.
        var failingChannel = new Mock<IChannel>();
        failingChannel.Setup(c => c.ExchangeDeclareAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<IDictionary<string, object?>?>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("declare failed"));
        failingChannel.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var workingChannel = NewWorkingChannelMock();

        var attempts = new Queue<IChannel>([failingChannel.Object, workingChannel.Object]);
        var connection = new Mock<IRabbitMqConnection>();
        connection.Setup(x => x.CreateChannelAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => attempts.Dequeue());

        var sut = new RabbitMqConsumer(connection.Object, NullLogger<RabbitMqConsumer>.Instance, EmptyConfig());

        var result = await sut.SetUpChannelWithRetryAsync("queue-name", "routing.key", CancellationToken.None);

        Assert.Same(workingChannel.Object, result);
        failingChannel.Verify(c => c.DisposeAsync(), Times.Once);
    }
}
