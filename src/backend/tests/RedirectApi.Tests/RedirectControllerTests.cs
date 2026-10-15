using Common;
using Common.Models;
using Contracts.Events;
using Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using RedirectApi;
using RedirectApi.Controllers;

namespace RedirectApi.Tests;

public class RedirectControllerTests
{
    private static DatabaseContext NewContext()
    {
        var options = new DbContextOptionsBuilder<DatabaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new DatabaseContext(options);
    }

    private static RedirectController NewController(
        DatabaseContext context,
        out Mock<IEntityCacheService<Link>> cache,
        out Mock<IEventDispatcher> dispatcher,
        out Mock<IClickCounterService> clickCounter,
        out Mock<ILinkDeletionMarkerStore> deletionMarkers)
    {
        cache = new Mock<IEntityCacheService<Link>>();
        dispatcher = new Mock<IEventDispatcher>();
        clickCounter = new Mock<IClickCounterService>();
        deletionMarkers = new Mock<ILinkDeletionMarkerStore>();
        // Default: no tombstone for any hash - individual tests opt into one explicitly.
        deletionMarkers.Setup(x => x.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LinkDeletionMarker?)null);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("short.example");
        httpContext.Request.Path = "/abc12345";

        return new RedirectController(context, cache.Object, dispatcher.Object, clickCounter.Object, deletionMarkers.Object, NullLogger<RedirectController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

    private static RedirectController NewController(
        DatabaseContext context,
        out Mock<IEntityCacheService<Link>> cache,
        out Mock<IEventDispatcher> dispatcher,
        out Mock<IClickCounterService> clickCounter) =>
        NewController(context, out cache, out dispatcher, out clickCounter, out _);

    [Fact]
    public async Task RedirectToOrigin_LinkFound_Returns302ToOriginalLink()
    {
        await using var context = NewContext();
        var link = new Link("abc12345", "https://example.com/target", "abc12345", DateTime.UtcNow, null);
        var sut = NewController(context, out var cache, out _, out _);
        cache.Setup(x => x.GetOrFetch("abc12345", It.IsAny<Func<Task<Link?>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(link);

        var result = await sut.RedirectToOrigin("abc12345", CancellationToken.None);

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("https://example.com/target", redirect.Url);
        Assert.False(redirect.Permanent);
    }

    [Fact]
    public async Task RedirectToOrigin_LinkFound_PublishesClickTrackedEvent()
    {
        await using var context = NewContext();
        var link = new Link("abc12345", "https://example.com/target", "abc12345", DateTime.UtcNow, null);
        var sut = NewController(context, out var cache, out var dispatcher, out _);
        cache.Setup(x => x.GetOrFetch("abc12345", It.IsAny<Func<Task<Link?>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(link);

        await sut.RedirectToOrigin("abc12345", CancellationToken.None);

        dispatcher.Verify(
            x => x.DispatchAsync(
                It.Is<ClickTrackedEvent>(e => e.Hash == "abc12345" && e.OutboundLink == "https://example.com/target" && e.Id != Guid.Empty),
                Topics.ClickTracked,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RedirectToOrigin_OwnedLink_RecordsClickForTheOwner()
    {
        var ownerId = Guid.NewGuid();
        await using var context = NewContext();
        var link = new Link("abc12345", "https://example.com/target", "abc12345", DateTime.UtcNow, ownerId);
        var sut = NewController(context, out var cache, out _, out var clickCounter);
        cache.Setup(x => x.GetOrFetch("abc12345", It.IsAny<Func<Task<Link?>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(link);

        await sut.RedirectToOrigin("abc12345", CancellationToken.None);

        clickCounter.Verify(
            x => x.RecordClickAsync("abc12345", ownerId, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RedirectToOrigin_AnonymousLink_RecordsClickWithoutAnOwner()
    {
        await using var context = NewContext();
        var link = new Link("abc12345", "https://example.com/target", "abc12345", DateTime.UtcNow, null);
        var sut = NewController(context, out var cache, out _, out var clickCounter);
        cache.Setup(x => x.GetOrFetch("abc12345", It.IsAny<Func<Task<Link?>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(link);

        await sut.RedirectToOrigin("abc12345", CancellationToken.None);

        clickCounter.Verify(
            x => x.RecordClickAsync("abc12345", null, It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RedirectToOrigin_CacheMiss_FetchesFromDatabase()
    {
        await using var context = NewContext();
        var stored = new Link("abc12345", "https://example.com/target", "abc12345", DateTime.UtcNow, null);
        context.Links.Add(stored);
        await context.SaveChangesAsync();
        var sut = NewController(context, out var cache, out _, out _);
        cache.Setup(x => x.GetOrFetch("abc12345", It.IsAny<Func<Task<Link?>>>(), It.IsAny<CancellationToken>()))
            .Returns<string, Func<Task<Link?>>, CancellationToken>((_, fetch, _) => fetch());

        var result = await sut.RedirectToOrigin("abc12345", CancellationToken.None);

        var redirect = Assert.IsType<RedirectResult>(result);
        Assert.Equal("https://example.com/target", redirect.Url);
    }

    [Fact]
    public async Task RedirectToOrigin_CacheMiss_FetchDoesNotTrackReturnedEntity()
    {
        // The DB fetch behind a cache miss is a pure read on this hot path - the entity is only ever
        // serialized into the cache and returned to the client, never mutated or saved back. Without
        // AsNoTracking, EF's change tracker snapshots it anyway, for no benefit, on every cache miss.
        await using var context = NewContext();
        var stored = new Link("abc12345", "https://example.com/target", "abc12345", DateTime.UtcNow, null);
        context.Links.Add(stored);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var sut = NewController(context, out var cache, out _, out _);
        cache.Setup(x => x.GetOrFetch("abc12345", It.IsAny<Func<Task<Link?>>>(), It.IsAny<CancellationToken>()))
            .Returns<string, Func<Task<Link?>>, CancellationToken>((_, fetch, _) => fetch());

        await sut.RedirectToOrigin("abc12345", CancellationToken.None);

        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Fact]
    public async Task RedirectToOrigin_LinkNotFound_ReturnsNotFoundAndDoesNotPublishOrIncrement()
    {
        await using var context = NewContext();
        var sut = NewController(context, out var cache, out var dispatcher, out var clickCounter);
        cache.Setup(x => x.GetOrFetch("missing", It.IsAny<Func<Task<Link?>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Link?)null);

        var result = await sut.RedirectToOrigin("missing", CancellationToken.None);

        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal(StatusCodes.Status404NotFound, content.StatusCode);
        dispatcher.Verify(
            x => x.DispatchAsync(It.IsAny<ClickTrackedEvent>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        clickCounter.Verify(
            x => x.RecordClickAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RedirectToOrigin_TombstonePresent_ReturnsGoneWithoutTouchingTheLinkCache()
    {
        await using var context = NewContext();
        var sut = NewController(context, out var cache, out var dispatcher, out var clickCounter, out var deletionMarkers);
        deletionMarkers.Setup(x => x.GetAsync("deleted-hash", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LinkDeletionMarker(DateTime.UtcNow, DeletedReason.Manual));

        var result = await sut.RedirectToOrigin("deleted-hash", CancellationToken.None);

        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal(StatusCodes.Status410Gone, content.StatusCode);
        Assert.Equal("text/html; charset=utf-8", content.ContentType);
        cache.Verify(
            x => x.GetOrFetch(It.IsAny<string>(), It.IsAny<Func<Task<Link?>>>(), It.IsAny<CancellationToken>()),
            Times.Never);
        dispatcher.Verify(
            x => x.DispatchAsync(It.IsAny<ClickTrackedEvent>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        clickCounter.Verify(
            x => x.RecordClickAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RedirectToOrigin_LinkHasDeletedAtSet_ReturnsGoneEvenWithoutAConfirmedTombstone()
    {
        // The live DeletedAt check on the Link row itself, independent of the tombstone - covers
        // the case where RedisLinkDeletionMarkerStore.ConfirmAsync failed open (Redis hiccup) but
        // Postgres, the real source of truth, already has the deletion.
        await using var context = NewContext();
        var link = new Link("abc12345", "https://example.com/target", "abc12345", DateTime.UtcNow, null, DeletedAt: DateTime.UtcNow);
        var sut = NewController(context, out var cache, out var dispatcher, out _);
        cache.Setup(x => x.GetOrFetch("abc12345", It.IsAny<Func<Task<Link?>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(link);

        var result = await sut.RedirectToOrigin("abc12345", CancellationToken.None);

        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal(StatusCodes.Status410Gone, content.StatusCode);
        dispatcher.Verify(
            x => x.DispatchAsync(It.IsAny<ClickTrackedEvent>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RedirectToOrigin_LinkExpiresAtInThePast_ReturnsGoneEvenBeforeTheSweepJobRuns()
    {
        await using var context = NewContext();
        var link = new Link(
            "abc12345", "https://example.com/target", "abc12345", DateTime.UtcNow, null,
            ExpiresAt: DateTime.UtcNow.AddMinutes(-1));
        var sut = NewController(context, out var cache, out var dispatcher, out _);
        cache.Setup(x => x.GetOrFetch("abc12345", It.IsAny<Func<Task<Link?>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(link);

        var result = await sut.RedirectToOrigin("abc12345", CancellationToken.None);

        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal(StatusCodes.Status410Gone, content.StatusCode);
        dispatcher.Verify(
            x => x.DispatchAsync(It.IsAny<ClickTrackedEvent>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
