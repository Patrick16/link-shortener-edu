using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Infrastructure.Tests;

public class RabbitMqRetryWorkerTests
{
    private static RabbitMqRetryWorker NewSut(IMessageFallbackStore fallbackStore, IMessagePublisher publisher) =>
        new(fallbackStore, publisher, NullLogger<RabbitMqRetryWorker>.Instance);

    [Fact]
    public async Task PollOnceAsync_RepublishSucceeds_DeletesFromFallbackStore()
    {
        var message = new FallbackMessage("msg-1", "some.topic", "{}", DateTime.UtcNow);
        var fallbackStore = new Mock<IMessageFallbackStore>();
        fallbackStore.Setup(x => x.GetPendingAsync(It.IsAny<CancellationToken>())).ReturnsAsync([message]);
        var publisher = new Mock<IMessagePublisher>();
        publisher.Setup(x => x.TryRepublishAsync(message, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var sut = NewSut(fallbackStore.Object, publisher.Object);

        await sut.PollOnceAsync(CancellationToken.None);

        fallbackStore.Verify(x => x.DeleteAsync("msg-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task PollOnceAsync_RepublishFails_LeavesMessageInFallbackStore()
    {
        var message = new FallbackMessage("msg-1", "some.topic", "{}", DateTime.UtcNow);
        var fallbackStore = new Mock<IMessageFallbackStore>();
        fallbackStore.Setup(x => x.GetPendingAsync(It.IsAny<CancellationToken>())).ReturnsAsync([message]);
        var publisher = new Mock<IMessagePublisher>();
        publisher.Setup(x => x.TryRepublishAsync(message, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var sut = NewSut(fallbackStore.Object, publisher.Object);

        await sut.PollOnceAsync(CancellationToken.None);

        fallbackStore.Verify(x => x.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PollOnceAsync_NoPendingMessages_DoesNothing()
    {
        var fallbackStore = new Mock<IMessageFallbackStore>();
        fallbackStore.Setup(x => x.GetPendingAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var publisher = new Mock<IMessagePublisher>();
        var sut = NewSut(fallbackStore.Object, publisher.Object);

        await sut.PollOnceAsync(CancellationToken.None);

        publisher.Verify(
            x => x.TryRepublishAsync(It.IsAny<FallbackMessage>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PollOnceAsync_MultipleMessages_RetriesEachIndependently()
    {
        var succeeding = new FallbackMessage("msg-1", "some.topic", "{}", DateTime.UtcNow);
        var failing = new FallbackMessage("msg-2", "some.topic", "{}", DateTime.UtcNow);
        var fallbackStore = new Mock<IMessageFallbackStore>();
        fallbackStore.Setup(x => x.GetPendingAsync(It.IsAny<CancellationToken>())).ReturnsAsync([succeeding, failing]);
        var publisher = new Mock<IMessagePublisher>();
        publisher.Setup(x => x.TryRepublishAsync(succeeding, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        publisher.Setup(x => x.TryRepublishAsync(failing, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var sut = NewSut(fallbackStore.Object, publisher.Object);

        await sut.PollOnceAsync(CancellationToken.None);

        fallbackStore.Verify(x => x.DeleteAsync("msg-1", It.IsAny<CancellationToken>()), Times.Once);
        fallbackStore.Verify(x => x.DeleteAsync("msg-2", It.IsAny<CancellationToken>()), Times.Never);
    }
}
