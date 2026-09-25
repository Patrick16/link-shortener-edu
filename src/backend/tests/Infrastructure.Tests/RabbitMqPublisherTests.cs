using Moq;
using RabbitMQ.Client;

namespace Infrastructure.Tests;

public class RabbitMqPublisherTests
{
    private static Mock<IChannel> NewOpenChannelMock()
    {
        var channel = new Mock<IChannel>();
        channel.SetupGet(c => c.IsOpen).Returns(true);
        channel.Setup(c => c.ExchangeDeclareAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<IDictionary<string, object?>?>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        channel.Setup(c => c.BasicPublishAsync<BasicProperties>(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<BasicProperties>(),
                It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);
        channel.Setup(c => c.DisposeAsync()).Returns(ValueTask.CompletedTask);
        return channel;
    }

    private static Mock<IRabbitMqConnection> NewConnectionReturning(params IChannel[] channels)
    {
        var connection = new Mock<IRabbitMqConnection>();
        var queue = new Queue<IChannel>(channels);
        connection.Setup(x => x.CreateChannelAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => queue.Count > 0 ? queue.Dequeue() : throw new InvalidOperationException("no more channels configured"));
        return connection;
    }

    [Fact]
    public async Task PublishAsync_Success_DoesNotFallBackToStore()
    {
        var channel = NewOpenChannelMock();
        var connection = NewConnectionReturning(channel.Object);
        var fallbackStore = new Mock<IMessageFallbackStore>();
        var sut = new RabbitMqPublisher(connection.Object, fallbackStore.Object);

        await sut.PublishAsync(new { Value = "hi" }, "some.topic", CancellationToken.None);

        fallbackStore.Verify(
            x => x.SaveAsync(It.IsAny<FallbackMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PublishAsync_ChannelCreationFails_SavesToFallbackStore()
    {
        var connection = new Mock<IRabbitMqConnection>();
        connection.Setup(x => x.CreateChannelAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("broker unreachable"));
        var fallbackStore = new Mock<IMessageFallbackStore>();
        var sut = new RabbitMqPublisher(connection.Object, fallbackStore.Object);

        await sut.PublishAsync(new { Value = "hi" }, "some.topic", CancellationToken.None);

        fallbackStore.Verify(
            x => x.SaveAsync(It.Is<FallbackMessage>(m => m.Topic == "some.topic"), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PublishAsync_BasicPublishThrows_SavesToFallbackStoreAndDiscardsChannel()
    {
        var channel = NewOpenChannelMock();
        channel.Setup(c => c.BasicPublishAsync<BasicProperties>(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<BasicProperties>(),
                It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.FromException(new InvalidOperationException("publish failed")));
        var connection = NewConnectionReturning(channel.Object);
        var fallbackStore = new Mock<IMessageFallbackStore>();
        var sut = new RabbitMqPublisher(connection.Object, fallbackStore.Object);

        await sut.PublishAsync(new { Value = "hi" }, "some.topic", CancellationToken.None);

        fallbackStore.Verify(x => x.SaveAsync(It.IsAny<FallbackMessage>(), It.IsAny<CancellationToken>()), Times.Once);
        // A channel that just faulted must be discarded, not returned to the idle pool for reuse.
        channel.Verify(c => c.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task PublishAsync_HealthyChannel_IsReusedAcrossPublishes()
    {
        var channel = NewOpenChannelMock();
        var connection = NewConnectionReturning(channel.Object);
        var fallbackStore = new Mock<IMessageFallbackStore>();
        var sut = new RabbitMqPublisher(connection.Object, fallbackStore.Object);

        await sut.PublishAsync(new { }, "topic.a", CancellationToken.None);
        await sut.PublishAsync(new { }, "topic.b", CancellationToken.None);

        // Only one channel was ever handed out by the connection - the second publish reused the
        // first one from the idle pool instead of opening a new one.
        connection.Verify(x => x.CreateChannelAsync(It.IsAny<CancellationToken>()), Times.Once);
        channel.Verify(c => c.DisposeAsync(), Times.Never);
    }

    [Fact]
    public async Task PublishAsync_RepeatedExchangeDeclareFailures_DoesNotExhaustChannelPoolOrLeakChannels()
    {
        // Regression: a channel that opened successfully but then failed ExchangeDeclareAsync used
        // to never get disposed, and the pool's semaphore slot was only released on the *next*
        // caller's success path - so every failed setup permanently lost one of the pool's 16 fixed
        // slots. Enough failures in a row used to exhaust the pool, and every publish after that
        // would block on the semaphore forever.
        var disposedChannels = 0;
        var connection = new Mock<IRabbitMqConnection>();
        connection.Setup(x => x.CreateChannelAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var channel = new Mock<IChannel>();
                channel.Setup(c => c.ExchangeDeclareAsync(
                        It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<bool>(),
                        It.IsAny<IDictionary<string, object?>?>(), It.IsAny<bool>(), It.IsAny<bool>(),
                        It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new InvalidOperationException("declare failed"));
                channel.Setup(c => c.DisposeAsync())
                    .Callback(() => Interlocked.Increment(ref disposedChannels))
                    .Returns(ValueTask.CompletedTask);
                return channel.Object;
            });
        var fallbackStore = new Mock<IMessageFallbackStore>();
        var sut = new RabbitMqPublisher(connection.Object, fallbackStore.Object);

        // One more publish than the pool's fixed capacity (16) - if a slot leaked on every
        // failure, the pool would already be fully exhausted well before this many calls.
        for (var i = 0; i < 17; i++)
        {
            await sut.PublishAsync(new { }, "some.topic", CancellationToken.None);
        }

        // The 18th call is the actual regression check: it must complete within a short timeout
        // (falling back to the store like every other failed publish) rather than hang forever
        // waiting on an exhausted semaphore.
        var publishTask = sut.PublishAsync(new { }, "some.topic", CancellationToken.None);
        var completed = await Task.WhenAny(publishTask, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(publishTask, completed);

        Assert.Equal(18, disposedChannels);
        fallbackStore.Verify(
            x => x.SaveAsync(It.IsAny<FallbackMessage>(), It.IsAny<CancellationToken>()), Times.Exactly(18));
    }

    [Fact]
    public async Task TryRepublishAsync_Success_ReturnsTrue()
    {
        var channel = NewOpenChannelMock();
        var connection = NewConnectionReturning(channel.Object);
        var fallbackStore = new Mock<IMessageFallbackStore>();
        var sut = new RabbitMqPublisher(connection.Object, fallbackStore.Object);
        var message = new FallbackMessage("msg-1", "some.topic", "{}", DateTime.UtcNow);

        var result = await sut.TryRepublishAsync(message, CancellationToken.None);

        Assert.True(result);
    }

    [Fact]
    public async Task TryRepublishAsync_Failure_ReturnsFalseWithoutTouchingFallbackStore()
    {
        var connection = new Mock<IRabbitMqConnection>();
        connection.Setup(x => x.CreateChannelAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("still unreachable"));
        var fallbackStore = new Mock<IMessageFallbackStore>();
        var sut = new RabbitMqPublisher(connection.Object, fallbackStore.Object);
        var message = new FallbackMessage("msg-1", "some.topic", "{}", DateTime.UtcNow);

        var result = await sut.TryRepublishAsync(message, CancellationToken.None);

        Assert.False(result);
        // The retry worker (not the publisher) decides what to do with the fallback store on a
        // failed retry - TryRepublishAsync itself must not touch it either way.
        fallbackStore.Verify(
            x => x.SaveAsync(It.IsAny<FallbackMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DisposeAsync_DisposesEveryIdleChannel()
    {
        var channel = NewOpenChannelMock();
        var connection = NewConnectionReturning(channel.Object);
        var fallbackStore = new Mock<IMessageFallbackStore>();
        var sut = new RabbitMqPublisher(connection.Object, fallbackStore.Object);
        await sut.PublishAsync(new { }, "some.topic", CancellationToken.None);

        await sut.DisposeAsync();

        channel.Verify(c => c.DisposeAsync(), Times.Once);
    }
}
