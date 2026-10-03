using Moq;

namespace Infrastructure.Tests;

public class LocalPublishQueueTests
{
    [Fact]
    public async Task EnqueueAsync_ThenDrained_PublishInvokesPublishAsyncWithTheQueuedMessageAndTopic()
    {
        var sut = new LocalPublishQueue();
        var message = new { Value = "hi" };

        await sut.EnqueueAsync(message, "some.topic", CancellationToken.None);
        var queued = await sut.Reader.ReadAsync();

        var publisher = new Mock<IMessagePublisher>();
        await queued.Publish(publisher.Object, CancellationToken.None);

        publisher.Verify(x => x.PublishAsync(message, "some.topic", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EnqueueAsync_QueueAtCapacity_BlocksUntilAnItemIsRead()
    {
        // Regression guard for the FullMode = Wait choice: a full queue must backpressure writers
        // instead of throwing or silently dropping the event.
        var sut = new LocalPublishQueue();
        for (var i = 0; i < 4096; i++)
        {
            await sut.EnqueueAsync(i, "some.topic", CancellationToken.None);
        }

        var blockedWrite = sut.EnqueueAsync(4096, "some.topic", CancellationToken.None).AsTask();
        var winner = await Task.WhenAny(blockedWrite, Task.Delay(TimeSpan.FromMilliseconds(200)));
        Assert.NotSame(blockedWrite, winner);

        await sut.Reader.ReadAsync();

        var completed = await Task.WhenAny(blockedWrite, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(blockedWrite, completed);
    }

    [Fact]
    public async Task Complete_LetsAlreadyQueuedItemsDrainThenTheReaderCompletes()
    {
        var sut = new LocalPublishQueue();
        await sut.EnqueueAsync("one", "topic.a", CancellationToken.None);
        await sut.EnqueueAsync("two", "topic.a", CancellationToken.None);

        sut.Complete();

        var drainedCount = 0;
        await foreach (var _ in sut.Reader.ReadAllAsync())
        {
            drainedCount++;
        }

        Assert.Equal(2, drainedCount);
    }
}
