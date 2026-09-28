using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Infrastructure.Tests;

public record TestMessage(string Value);

public class RabbitMqConsumerTests
{
    private static IConfiguration EmptyConfig() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();

    private static IConfiguration BatchConfig(int batchSize, int batchTimeoutMs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RabbitMq:BatchSize"] = batchSize.ToString(),
                ["RabbitMq:BatchTimeoutMs"] = batchTimeoutMs.ToString(),
            })
            .Build();

    private static BasicDeliverEventArgs NewDelivery(string messageId, object payload, ulong deliveryTag = 1) =>
        new(
            consumerTag: "consumer-tag",
            deliveryTag: deliveryTag,
            redelivered: deliveryTag > 1,
            exchange: "events",
            routingKey: "some.topic",
            properties: new BasicProperties { MessageId = messageId },
            body: Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));

    private static Mock<IChannel> NewChannelMock()
    {
        var channel = new Mock<IChannel>();
        channel.Setup(c => c.BasicAckAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);
        channel.Setup(c => c.BasicNackAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);
        channel.Setup(c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<BasicProperties>(),
                It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()))
            .Returns(ValueTask.CompletedTask);
        return channel;
    }

    private static Mock<IChannel> NewWorkingChannelMock()
    {
        var channel = NewChannelMock();
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

    [Fact]
    public async Task ReceiveDeliveryAsync_ValidMessage_BuffersItWithoutAckingYet()
    {
        var channel = NewChannelMock();
        var sut = new RabbitMqConsumer(Mock.Of<IRabbitMqConnection>(), NullLogger<RabbitMqConsumer>.Instance, EmptyConfig());
        var buffer = Channel.CreateUnbounded<BufferedDelivery<TestMessage>>();
        var ea = NewDelivery("msg-1", new TestMessage("hello"), deliveryTag: 7);

        await sut.ReceiveDeliveryAsync<TestMessage>(channel.Object, "queue-name.dead", buffer.Writer, ea, CancellationToken.None);

        Assert.True(buffer.Reader.TryRead(out var buffered));
        Assert.Equal("msg-1", buffered!.MessageId);
        Assert.Equal(7UL, buffered.DeliveryTag);
        Assert.Equal("hello", buffered.Message.Value);
        channel.Verify(c => c.BasicAckAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        channel.Verify(
            c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<BasicProperties>(),
                It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ReceiveDeliveryAsync_MalformedJson_DeadLettersAndAcksInsteadOfBuffering()
    {
        var channel = NewChannelMock();
        var sut = new RabbitMqConsumer(Mock.Of<IRabbitMqConnection>(), NullLogger<RabbitMqConsumer>.Instance, EmptyConfig());
        var buffer = Channel.CreateUnbounded<BufferedDelivery<TestMessage>>();
        var ea = new BasicDeliverEventArgs(
            consumerTag: "consumer-tag",
            deliveryTag: 1,
            redelivered: false,
            exchange: "events",
            routingKey: "some.topic",
            properties: new BasicProperties { MessageId = "bad-msg" },
            body: Encoding.UTF8.GetBytes("not-json"));

        await sut.ReceiveDeliveryAsync<TestMessage>(channel.Object, "queue-name.dead", buffer.Writer, ea, CancellationToken.None);

        Assert.False(buffer.Reader.TryRead(out _));
        channel.Verify(c => c.BasicAckAsync(1, false, It.IsAny<CancellationToken>()), Times.Once);
        channel.Verify(
            c => c.BasicPublishAsync(
                "", "queue-name.dead", false, It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task FlushBatchAsync_HandlerSucceeds_AcksWholeBatchOnceWithHighestDeliveryTag()
    {
        var channel = NewChannelMock();
        var sut = new RabbitMqConsumer(Mock.Of<IRabbitMqConnection>(), NullLogger<RabbitMqConsumer>.Instance, EmptyConfig());
        var batch = new List<BufferedDelivery<TestMessage>>
        {
            new(1, "msg-1", new TestMessage("a")),
            new(2, "msg-2", new TestMessage("b")),
            new(3, "msg-3", new TestMessage("c")),
        };

        await sut.FlushBatchAsync(
            channel.Object, "queue-name", "queue-name.dead", batch,
            (_, _) => Task.FromResult(BatchOutcome.Success), CancellationToken.None);

        channel.Verify(c => c.BasicAckAsync(3, true, It.IsAny<CancellationToken>()), Times.Once);
        channel.Verify(c => c.BasicAckAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Once);
        channel.Verify(c => c.BasicNackAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
        channel.Verify(
            c => c.BasicPublishAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<BasicProperties>(),
                It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task FlushBatchAsync_HandlerReportsPoison_DeadLettersPoisonThenAcksWholeBatch()
    {
        var channel = NewChannelMock();
        var sut = new RabbitMqConsumer(Mock.Of<IRabbitMqConnection>(), NullLogger<RabbitMqConsumer>.Instance, EmptyConfig());
        var batch = new List<BufferedDelivery<TestMessage>>
        {
            new(1, "msg-1", new TestMessage("good")),
            new(2, "poison-msg", new TestMessage("bad")),
        };

        await sut.FlushBatchAsync(
            channel.Object, "queue-name", "queue-name.dead", batch,
            (_, _) => Task.FromResult(BatchOutcome.WithPoison(["poison-msg"])), CancellationToken.None);

        channel.Verify(
            c => c.BasicPublishAsync(
                "", "queue-name.dead", false, It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()),
            Times.Once);
        channel.Verify(c => c.BasicAckAsync(2, true, It.IsAny<CancellationToken>()), Times.Once);
        channel.Verify(c => c.BasicNackAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FlushBatchAsync_HandlerThrows_NacksWholeBatchWithRequeueBelowMaxAttempts()
    {
        var channel = NewChannelMock();
        var sut = new RabbitMqConsumer(Mock.Of<IRabbitMqConnection>(), NullLogger<RabbitMqConsumer>.Instance, EmptyConfig());
        var batch = new List<BufferedDelivery<TestMessage>>
        {
            new(1, "msg-1", new TestMessage("a")),
            new(2, "msg-2", new TestMessage("b")),
        };
        Task<BatchOutcome> Handler(IReadOnlyList<BatchItem<TestMessage>> _, CancellationToken _1) =>
            throw new InvalidOperationException("db unreachable");

        await sut.FlushBatchAsync(channel.Object, "queue-name", "queue-name.dead", batch, Handler, CancellationToken.None);

        channel.Verify(c => c.BasicNackAsync(1, false, true, It.IsAny<CancellationToken>()), Times.Once);
        channel.Verify(c => c.BasicNackAsync(2, false, true, It.IsAny<CancellationToken>()), Times.Once);
        channel.Verify(c => c.BasicAckAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FlushBatchAsync_SamePoisonMessageFailsAcrossFiveBatches_DeadLettersInsteadOfRetryingForever()
    {
        // Mirrors the old single-message regression test, one level up: a message that can never
        // succeed must eventually stop being requeued no matter how many separate batches it lands
        // in - RabbitMQ redelivers a requeued message immediately, so without this it would hot-loop.
        var channel = NewChannelMock();
        var sut = new RabbitMqConsumer(Mock.Of<IRabbitMqConnection>(), NullLogger<RabbitMqConsumer>.Instance, EmptyConfig());
        Task<BatchOutcome> Handler(IReadOnlyList<BatchItem<TestMessage>> _, CancellationToken _1) =>
            throw new InvalidOperationException("always fails");

        for (var i = 0; i < 5; i++)
        {
            var batch = new List<BufferedDelivery<TestMessage>> { new((ulong)(i + 1), "poison-msg", new TestMessage("bad")) };
            await sut.FlushBatchAsync(channel.Object, "queue-name", "queue-name.dead", batch, Handler, CancellationToken.None);
        }

        channel.Verify(c => c.BasicNackAsync(It.IsAny<ulong>(), false, true, It.IsAny<CancellationToken>()), Times.Exactly(4));
        channel.Verify(c => c.BasicAckAsync(5, false, It.IsAny<CancellationToken>()), Times.Once);
        channel.Verify(
            c => c.BasicPublishAsync(
                "", "queue-name.dead", false, It.IsAny<BasicProperties>(), It.IsAny<ReadOnlyMemory<byte>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task FillBatchAsync_ReachesBatchSize_ReturnsAsSoonAsFullWithoutWaitingForTimeout()
    {
        var sut = new RabbitMqConsumer(Mock.Of<IRabbitMqConnection>(), NullLogger<RabbitMqConsumer>.Instance, BatchConfig(3, 5000));
        var buffer = Channel.CreateUnbounded<BufferedDelivery<TestMessage>>();
        for (var i = 0; i < 3; i++)
        {
            await buffer.Writer.WriteAsync(new BufferedDelivery<TestMessage>((ulong)(i + 1), $"msg-{i}", new TestMessage("x")));
        }
        var batch = new List<BufferedDelivery<TestMessage>>();

        var stopwatch = Stopwatch.StartNew();
        var hasMore = await sut.FillBatchAsync(buffer.Reader, batch, CancellationToken.None);
        stopwatch.Stop();

        Assert.True(hasMore);
        Assert.Equal(3, batch.Count);
        Assert.True(stopwatch.ElapsedMilliseconds < 4000, "should return once full, not wait out the 5s timeout");
    }

    [Fact]
    public async Task FillBatchAsync_TimeoutElapsesBeforeBatchSize_ReturnsPartialBatch()
    {
        var sut = new RabbitMqConsumer(Mock.Of<IRabbitMqConnection>(), NullLogger<RabbitMqConsumer>.Instance, BatchConfig(100, 100));
        var buffer = Channel.CreateUnbounded<BufferedDelivery<TestMessage>>();
        await buffer.Writer.WriteAsync(new BufferedDelivery<TestMessage>(1, "msg-1", new TestMessage("x")));
        var batch = new List<BufferedDelivery<TestMessage>>();

        var hasMore = await sut.FillBatchAsync(buffer.Reader, batch, CancellationToken.None);

        Assert.True(hasMore);
        Assert.Single(batch);
    }
}
