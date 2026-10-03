using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Infrastructure.Tests;

public class LocalPublishQueueWorkerTests
{
    [Fact]
    public async Task StartAsync_QueuedItem_IsPublished()
    {
        var queue = new LocalPublishQueue();
        var publisher = new Mock<IMessagePublisher>();
        var sut = new LocalPublishQueueWorker(queue, publisher.Object, NullLogger<LocalPublishQueueWorker>.Instance);

        await sut.StartAsync(CancellationToken.None);
        await queue.EnqueueAsync("payload", "some.topic", CancellationToken.None);

        await WaitUntil(() => publisher.Invocations.Count > 0);
        publisher.Verify(x => x.PublishAsync("payload", "some.topic", It.IsAny<CancellationToken>()), Times.Once);

        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartAsync_PublishThrowsForOneItem_StillProcessesLaterItems()
    {
        // A worker loop that dies on the first unexpected exception would stop draining the queue
        // entirely for every event still waiting behind it - this guards against that regression.
        var queue = new LocalPublishQueue();
        var publisher = new Mock<IMessagePublisher>();
        publisher.Setup(x => x.PublishAsync("bad", "some.topic", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        publisher.Setup(x => x.PublishAsync("good", "some.topic", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var sut = new LocalPublishQueueWorker(queue, publisher.Object, NullLogger<LocalPublishQueueWorker>.Instance);

        await sut.StartAsync(CancellationToken.None);
        await queue.EnqueueAsync("bad", "some.topic", CancellationToken.None);
        await queue.EnqueueAsync("good", "some.topic", CancellationToken.None);

        await WaitUntil(() => publisher.Invocations.Count >= 2);
        publisher.Verify(x => x.PublishAsync("good", "some.topic", It.IsAny<CancellationToken>()), Times.Once);

        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_ItemAlreadyQueued_IsDrainedBeforeReturning()
    {
        var queue = new LocalPublishQueue();
        var publisher = new Mock<IMessagePublisher>();
        var sut = new LocalPublishQueueWorker(queue, publisher.Object, NullLogger<LocalPublishQueueWorker>.Instance);

        await sut.StartAsync(CancellationToken.None);
        await queue.EnqueueAsync("payload", "some.topic", CancellationToken.None);

        await sut.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);

        publisher.Verify(x => x.PublishAsync("payload", "some.topic", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartAsync_ProcessesQueuedItem_PassesThroughTheTraceContextCapturedAtEnqueueTime()
    {
        // Mirrors the real ordering: Activity.Current is the request's own, still-live Activity at
        // enqueue time (ASP.NET only Stop()s it once the response finishes writing, well after this).
        // EnqueueAsync must capture the ActivityContext value then and there - Activity.Current can
        // never be set back to an Activity once it's finished, so capturing the Activity reference
        // itself (instead of its context) would silently lose the parent by the time a worker gets to
        // it, as confirmed by this test failing when written that way first.
        var previous = Activity.Current;
        using var requestActivity = new Activity("http-request");
        requestActivity.Start();

        var queue = new LocalPublishQueue();
        await queue.EnqueueAsync("payload", "some.topic", CancellationToken.None);
        var requestContext = requestActivity.Context;
        requestActivity.Stop();
        Activity.Current = previous;

        ActivityContext? observedParentContext = null;
        var publisher = new Mock<IMessagePublisher>();
        publisher
            .Setup(x => x.PublishAsync("payload", "some.topic", It.IsAny<CancellationToken>(), It.IsAny<ActivityContext?>()))
            .Callback<string, string, CancellationToken, ActivityContext?>((_, _, _, parentContext) => observedParentContext = parentContext)
            .Returns(Task.CompletedTask);
        var sut = new LocalPublishQueueWorker(queue, publisher.Object, NullLogger<LocalPublishQueueWorker>.Instance);

        await sut.StartAsync(CancellationToken.None);
        await WaitUntil(() => observedParentContext is not null);
        await sut.StopAsync(CancellationToken.None);

        Assert.Equal(requestContext, observedParentContext);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "Condition was not met within the timeout.");
    }
}
