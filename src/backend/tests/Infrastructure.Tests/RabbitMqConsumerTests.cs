using System.Text;
using System.Text.Json;
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
        return channel;
    }

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

    [Fact]
    public async Task HandleDeliveryAsync_HandlerSucceeds_AcksAndDoesNotNack()
    {
        var channel = NewChannelMock();
        var sut = new RabbitMqConsumer(Mock.Of<IRabbitMqConnection>(), NullLogger<RabbitMqConsumer>.Instance, EmptyConfig());
        var ea = NewDelivery("msg-1", new TestMessage("hello"));

        await sut.HandleDeliveryAsync<TestMessage>(
            channel.Object, "queue-name", "routing.key", ea, (_, _) => Task.CompletedTask, CancellationToken.None);

        channel.Verify(c => c.BasicAckAsync(1, false, It.IsAny<CancellationToken>()), Times.Once);
        channel.Verify(c => c.BasicNackAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleDeliveryAsync_HandlerFails_NacksWithRequeueBelowMaxAttempts()
    {
        var channel = NewChannelMock();
        var sut = new RabbitMqConsumer(Mock.Of<IRabbitMqConnection>(), NullLogger<RabbitMqConsumer>.Instance, EmptyConfig());
        var ea = NewDelivery("poison-msg", new TestMessage("bad"));
        Task Handler(TestMessage _, CancellationToken _1) => throw new InvalidOperationException("always fails");

        await sut.HandleDeliveryAsync<TestMessage>(channel.Object, "queue-name", "routing.key", ea, Handler, CancellationToken.None);

        channel.Verify(c => c.BasicNackAsync(1, false, true, It.IsAny<CancellationToken>()), Times.Once);
        channel.Verify(c => c.BasicAckAsync(It.IsAny<ulong>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task HandleDeliveryAsync_SamePoisonMessageFailsRepeatedly_StopsRequeueingAfterMaxAttemptsInsteadOfLoopingForever()
    {
        // This is the core regression: a message that can never succeed (bad payload, a handler bug)
        // used to be nacked with requeue:true unconditionally, and RabbitMQ redelivers a requeued
        // message immediately - an unbounded hot loop that pins the consumer's CPU and never drains
        // the queue. Simulates RabbitMQ's redelivery by calling HandleDeliveryAsync repeatedly for the
        // same MessageId (as real redeliveries of the same logical message would be) and asserts the
        // loop actually terminates: after MaxDeliveryAttempts, the message is nacked WITHOUT requeue
        // instead of forever with requeue:true.
        var channel = NewChannelMock();
        var sut = new RabbitMqConsumer(Mock.Of<IRabbitMqConnection>(), NullLogger<RabbitMqConsumer>.Instance, EmptyConfig());
        Task Handler(TestMessage _, CancellationToken _1) => throw new InvalidOperationException("always fails");

        // A real broker only keeps redelivering while each nack requeues the message - once one nack
        // sets requeue:false, RabbitMQ drops it and there is no delivery #6. Ten iterations (well past
        // MaxDeliveryAttempts) proves the loop itself would still terminate correctly even if it were
        // somehow redelivered again, without relying on that broker behavior to end the test early.
        for (var i = 0; i < 10; i++)
        {
            var ea = NewDelivery("poison-msg", new TestMessage("bad"), deliveryTag: (ulong)(i + 1));
            await sut.HandleDeliveryAsync<TestMessage>(channel.Object, "queue-name", "routing.key", ea, Handler, CancellationToken.None);
        }

        // Requeued for the first (MaxDeliveryAttempts - 1) attempts, then dropped (requeue: false) -
        // and, since the attempt counter is cleared on every drop, the cycle of 4 requeues + 1 drop
        // repeats rather than requeueing forever.
        channel.Verify(c => c.BasicNackAsync(It.IsAny<ulong>(), false, true, It.IsAny<CancellationToken>()), Times.Exactly(8));
        channel.Verify(c => c.BasicNackAsync(It.IsAny<ulong>(), false, false, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task HandleDeliveryAsync_HandlerSucceedsAfterEarlierFailure_ClearsAttemptCounter()
    {
        // A message that fails once (a genuinely transient blip) and then succeeds on redelivery must
        // not carry its old attempt count into some later, unrelated delivery that happens to reuse
        // the same MessageId - the counter is cleared on success.
        var channel = NewChannelMock();
        var sut = new RabbitMqConsumer(Mock.Of<IRabbitMqConnection>(), NullLogger<RabbitMqConsumer>.Instance, EmptyConfig());
        var failingEa = NewDelivery("msg-1", new TestMessage("x"), deliveryTag: 1);
        await sut.HandleDeliveryAsync<TestMessage>(
            channel.Object, "queue-name", "routing.key", failingEa,
            (_, _) => throw new InvalidOperationException("transient"), CancellationToken.None);

        var succeedingEa = NewDelivery("msg-1", new TestMessage("x"), deliveryTag: 2);
        await sut.HandleDeliveryAsync<TestMessage>(
            channel.Object, "queue-name", "routing.key", succeedingEa, (_, _) => Task.CompletedTask, CancellationToken.None);

        channel.Verify(c => c.BasicAckAsync(2, false, It.IsAny<CancellationToken>()), Times.Once);

        // A brand-new delivery for the same MessageId now gets a fresh count of attempts, not a
        // continuation of the count from before the earlier success.
        for (var i = 0; i < 4; i++)
        {
            var ea = NewDelivery("msg-1", new TestMessage("x"), deliveryTag: (ulong)(i + 3));
            await sut.HandleDeliveryAsync<TestMessage>(
                channel.Object, "queue-name", "routing.key", ea,
                (_, _) => throw new InvalidOperationException("failing again"), CancellationToken.None);
        }

        channel.Verify(c => c.BasicNackAsync(It.IsAny<ulong>(), false, false, It.IsAny<CancellationToken>()), Times.Never);
    }
}
