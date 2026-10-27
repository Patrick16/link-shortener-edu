using Contracts.Events;
using Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ShortenerService;

namespace ShortenerService.Tests;

// ClickTrackedConsumer no longer touches Postgres itself (see ClickCountAggregator's own comment) -
// these tests assert against the shared ClickCountAggregator instead of a database. The actual
// Postgres write is ClickCountFlushWorker's job, covered separately in
// ClickCountFlushWorkerTests.
public sealed class ClickTrackedConsumerTests
{
    private static ClickTrackedEvent NewEvent(string hash = "abc12345") => new()
    {
        Id = Guid.NewGuid(),
        Hash = hash,
        InboundLink = "https://short.example/abc12345",
        OutboundLink = "https://example.com",
        ClickedAt = DateTime.UtcNow,
        UserAgent = "",
        Referrer = "",
    };

    private static List<BatchItem<ClickTrackedEvent>> NewBatch(params ClickTrackedEvent[] events) =>
        events.Select(e => new BatchItem<ClickTrackedEvent>(Guid.NewGuid().ToString(), e)).ToList();

    private static ClickTrackedConsumer NewSut(ClickCountAggregator aggregator) =>
        new(Mock.Of<IMessageConsumer>(), aggregator, NullLogger<ClickTrackedConsumer>.Instance);

    [Fact]
    public async Task HandleBatchAsync_SingleEvent_BuffersOneIncrementInTheAggregator()
    {
        var aggregator = new ClickCountAggregator();
        var sut = NewSut(aggregator);

        var outcome = await sut.HandleBatchAsync(NewBatch(NewEvent()), CancellationToken.None);

        Assert.Equal(BatchOutcome.Success, outcome);
        var pending = aggregator.DrainPending();
        Assert.Equal(1, pending["abc12345"]);
    }

    [Fact]
    public async Task HandleBatchAsync_MultipleEventsSameHashInOneBatch_AccumulatesIntoOneEntry()
    {
        var aggregator = new ClickCountAggregator();
        var sut = NewSut(aggregator);

        await sut.HandleBatchAsync(NewBatch(NewEvent(), NewEvent(), NewEvent()), CancellationToken.None);

        var pending = aggregator.DrainPending();
        Assert.Equal(3, pending["abc12345"]);
    }

    [Fact]
    public async Task HandleBatchAsync_MultipleBatches_AccumulatesAcrossBatchesUntilDrained()
    {
        // The whole point of this feature: increments from several consumed batches pile up in the
        // SAME aggregator entry instead of each batch triggering its own Postgres write - see
        // ClickCountAggregator's own comment for why that's what relieves row-lock contention.
        var aggregator = new ClickCountAggregator();
        var sut = NewSut(aggregator);

        await sut.HandleBatchAsync(NewBatch(NewEvent()), CancellationToken.None);
        await sut.HandleBatchAsync(NewBatch(NewEvent()), CancellationToken.None);
        await sut.HandleBatchAsync(NewBatch(NewEvent()), CancellationToken.None);

        var pending = aggregator.DrainPending();
        Assert.Equal(3, pending["abc12345"]);
    }

    [Fact]
    public async Task HandleBatchAsync_DifferentHashes_BuffersEachIndependently()
    {
        var aggregator = new ClickCountAggregator();
        var sut = NewSut(aggregator);

        await sut.HandleBatchAsync(NewBatch(NewEvent("hash0001"), NewEvent("hash0001"), NewEvent("hash0002")), CancellationToken.None);

        var pending = aggregator.DrainPending();
        Assert.Equal(2, pending["hash0001"]);
        Assert.Equal(1, pending["hash0002"]);
    }

    [Fact]
    public async Task HandleBatchAsync_UnknownHash_DoesNotThrow()
    {
        // ClickTrackedConsumer no longer knows (or cares) whether a hash exists - that check now
        // happens in ClickCountFlushWorker, once it actually tries to apply the UPDATE.
        var aggregator = new ClickCountAggregator();
        var sut = NewSut(aggregator);

        var exception = await Record.ExceptionAsync(
            () => sut.HandleBatchAsync(NewBatch(NewEvent("never-created")), CancellationToken.None));

        Assert.Null(exception);
    }
}
