using Common;
using Common.Models;
using Contracts.Events;
using Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ReportingService;

namespace ReportingService.Tests;

public class ClickTrackedConsumerTests
{
    private static ClickTrackedEvent NewEvent(Guid? id = null, string? ipAddress = null, string referrer = "") => new()
    {
        Id = id ?? Guid.NewGuid(),
        Hash = "abc12345",
        InboundLink = "https://short.example/abc12345",
        OutboundLink = "https://example.com",
        ClickedAt = DateTime.UtcNow,
        UserAgent = "",
        Referrer = referrer,
        IpAddress = ipAddress,
    };

    private static List<BatchItem<ClickTrackedEvent>> NewBatch(params ClickTrackedEvent[] events) =>
        events.Select(e => new BatchItem<ClickTrackedEvent>(Guid.NewGuid().ToString(), e)).ToList();

    private static ClickTrackedConsumer NewSut(
        Mock<IClickFactStore> clickFactStore,
        Mock<IGeoIpResolver>? geoIpResolver = null,
        Mock<IUserAgentParser>? userAgentParser = null) => new(
            Mock.Of<IMessageConsumer>(),
            clickFactStore.Object,
            (userAgentParser ?? new Mock<IUserAgentParser>()).Object,
            (geoIpResolver ?? new Mock<IGeoIpResolver>()).Object,
            NullLogger<ClickTrackedConsumer>.Instance);

    [Fact]
    public async Task HandleBatchAsync_NewClick_InsertsOneFact()
    {
        var store = new Mock<IClickFactStore>();
        var sut = NewSut(store);
        var @event = NewEvent();

        await sut.HandleBatchAsync(NewBatch(@event), CancellationToken.None);

        store.Verify(
            x => x.InsertManyAsync(
                It.Is<IReadOnlyCollection<ClickFact>>(f => f.Count == 1 && f.Single().Id == @event.Id && f.Single().Hash == "abc12345"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task HandleBatchAsync_DuplicateIdWithinOneBatch_InsertsOnce()
    {
        // Two deliveries for the same click Id can land in the same batch (e.g. a requeue racing a
        // fresh delivery) - must dedupe within the batch. Cross-batch redelivery is handled
        // downstream by ClickHouse's ReplacingMergeTree, not here - see the consumer's own comment.
        var store = new Mock<IClickFactStore>();
        var sut = NewSut(store);
        var @event = NewEvent();

        await sut.HandleBatchAsync(NewBatch(@event, @event with { }), CancellationToken.None);

        store.Verify(
            x => x.InsertManyAsync(It.Is<IReadOnlyCollection<ClickFact>>(f => f.Count == 1), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task HandleBatchAsync_MultipleClicks_ResolvesGeoIpConcurrentlyWithoutMixingUpResults()
    {
        // Regression: geo-IP resolution uses bounded parallelism (Parallel.ForEachAsync) - must
        // still match each click's own country back to that same click, not scramble results
        // across the batch under concurrency. Mirrors TrafficService's identical regression test.
        var geoIpResolver = new Mock<IGeoIpResolver>();
        geoIpResolver.Setup(x => x.ResolveAsync("1.1.1.1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GeoLocation("Australia", "Sydney"));
        geoIpResolver.Setup(x => x.ResolveAsync("2.2.2.2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GeoLocation("Germany", "Berlin"));
        IReadOnlyCollection<ClickFact>? inserted = null;
        var store = new Mock<IClickFactStore>();
        store.Setup(x => x.InsertManyAsync(It.IsAny<IReadOnlyCollection<ClickFact>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyCollection<ClickFact>, CancellationToken>((facts, _) => inserted = facts)
            .Returns(Task.CompletedTask);
        var sut = NewSut(store, geoIpResolver);
        var eventA = NewEvent(ipAddress: "1.1.1.1");
        var eventB = NewEvent(ipAddress: "2.2.2.2");

        await sut.HandleBatchAsync(NewBatch(eventA, eventB), CancellationToken.None);

        Assert.NotNull(inserted);
        Assert.Equal("Australia", inserted!.Single(f => f.Id == eventA.Id).Country);
        Assert.Equal("Germany", inserted!.Single(f => f.Id == eventB.Id).Country);
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("not a url", null)]
    [InlineData("https://news.example.com/article", "news.example.com")]
    public async Task HandleBatchAsync_VariousReferrers_ExtractsDomainOrNull(string referrer, string? expectedDomain)
    {
        IReadOnlyCollection<ClickFact>? inserted = null;
        var store = new Mock<IClickFactStore>();
        store.Setup(x => x.InsertManyAsync(It.IsAny<IReadOnlyCollection<ClickFact>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyCollection<ClickFact>, CancellationToken>((facts, _) => inserted = facts)
            .Returns(Task.CompletedTask);
        var sut = NewSut(store);

        await sut.HandleBatchAsync(NewBatch(NewEvent(referrer: referrer)), CancellationToken.None);

        Assert.Equal(expectedDomain, inserted!.Single().ReferrerDomain);
    }
}
