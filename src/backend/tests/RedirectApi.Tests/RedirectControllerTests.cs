using Common;
using Common.Models;
using Contracts.Events;
using Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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
        out Mock<ILinkDeletionMarkerStore> deletionMarkers,
        int? requestBudgetMs = null)
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

        // Left at the production default (2000ms) unless a test overrides it - only the
        // budget-exceeded tests need a short one, so the real timer they wait out stays in the
        // low tens of milliseconds instead of the full 2s.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(requestBudgetMs is { } ms
                ? [new KeyValuePair<string, string?>("Redirect:RequestBudgetMs", ms.ToString())]
                : [])
            .Build();

        return new RedirectController(context, cache.Object, dispatcher.Object, clickCounter.Object, deletionMarkers.Object, configuration, NullLogger<RedirectController>.Instance)
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
    public async Task RedirectToOrigin_OwnedLink_PublishesClickTrackedEventWithTheOwnersUserId()
    {
        // Feeds owner-scoped reporting (ReportingService writes this straight through to
        // ClickFact.UserId) - the same link.UserId value RecordsClickForTheOwner above already
        // verifies reaches the click counter.
        var ownerId = Guid.NewGuid();
        await using var context = NewContext();
        var link = new Link("abc12345", "https://example.com/target", "abc12345", DateTime.UtcNow, ownerId);
        var sut = NewController(context, out var cache, out var dispatcher, out _);
        cache.Setup(x => x.GetOrFetch("abc12345", It.IsAny<Func<Task<Link?>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(link);

        await sut.RedirectToOrigin("abc12345", CancellationToken.None);

        dispatcher.Verify(
            x => x.DispatchAsync(
                It.Is<ClickTrackedEvent>(e => e.UserId == ownerId),
                Topics.ClickTracked,
                It.IsAny<CancellationToken>()),
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

    // Awaits the token actually becoming cancelled instead of assuming it already is - a real
    // Redis/EF call behaves the same way (it doesn't throw the instant a cancellable call is made,
    // only once the token it was given actually fires), and this lets the budget-exceeded tests use
    // RequestController's real CancelAfter timer (just a very short one, via requestBudgetMs) rather
    // than faking an exception no real dependency call would throw synchronously.
    private static async Task<Link?> WaitForCancellationAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return null; // Unreachable - Task.Delay above only ever returns by throwing.
    }

    [Fact]
    public async Task RedirectToOrigin_CacheLookupExceedsRequestBudget_Returns503WithRetryAfterInsteadOfWaiting()
    {
        // Simulates a dependency (Redis/Postgres) that's still running but far past the point where
        // waiting on it is worthwhile - GetOrFetch hangs until RedirectController's own linked
        // budget token fires (20ms here, see NewController's requestBudgetMs), exactly as a real
        // cancelled Redis/EF call would.
        await using var context = NewContext();
        var sut = NewController(context, out var cache, out var dispatcher, out var clickCounter, out _, requestBudgetMs: 20);
        cache.Setup(x => x.GetOrFetch("abc12345", It.IsAny<Func<Task<Link?>>>(), It.IsAny<CancellationToken>()))
            .Returns<string, Func<Task<Link?>>, CancellationToken>((_, _, ct) => WaitForCancellationAsync(ct));

        var result = await sut.RedirectToOrigin("abc12345", CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, problem.StatusCode);
        var problemDetails = Assert.IsType<ProblemDetails>(problem.Value);
        Assert.Equal("Request budget exceeded.", problemDetails.Title);
        dispatcher.Verify(
            x => x.DispatchAsync(It.IsAny<ClickTrackedEvent>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        clickCounter.Verify(
            x => x.RecordClickAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RedirectToOrigin_CacheLookupExceedsRequestBudget_SetsRetryAfterHeader()
    {
        await using var context = NewContext();
        var sut = NewController(context, out var cache, out _, out _, out _, requestBudgetMs: 20);
        cache.Setup(x => x.GetOrFetch("abc12345", It.IsAny<Func<Task<Link?>>>(), It.IsAny<CancellationToken>()))
            .Returns<string, Func<Task<Link?>>, CancellationToken>((_, _, ct) => WaitForCancellationAsync(ct));

        await sut.RedirectToOrigin("abc12345", CancellationToken.None);

        Assert.Equal("1", (string?)sut.Response.Headers["Retry-After"]);
    }

    [Fact]
    public async Task RedirectToOrigin_AnotherRequestsBudgetFiresDuringCoalescedFetch_Returns503InsteadOfThrowing()
    {
        // Regression for a bug found in review: _cache.GetOrFetch coalesces concurrent misses for
        // the same hash behind one shared in-flight Task, driven by whichever request's call wins
        // the race (see EntityCacheService's own comment on _inFlightFetches). When THAT driver's
        // budget fires, every joiner observes an OperationCanceledException tied to the DRIVER's own
        // CancellationTokenSource - not this request's local budgetToken. The old filter
        // (`budgetToken.IsCancellationRequested && ...`) only recognized its own token firing, so a
        // joiner's exception propagated uncaught instead of becoming a clean 503. This request's own
        // budget is left at the long production default (no requestBudgetMs override) and its own
        // caller token is never cancelled - simulating exactly a joiner whose own deadline is nowhere
        // near firing, but who still gets cancelled because a sibling request's budget did.
        await using var context = NewContext();
        var sut = NewController(context, out var cache, out var dispatcher, out var clickCounter, out _);
        using var driversBudget = new CancellationTokenSource();
        cache.Setup(x => x.GetOrFetch("abc12345", It.IsAny<Func<Task<Link?>>>(), It.IsAny<CancellationToken>()))
            .Returns<string, Func<Task<Link?>>, CancellationToken>((_, _, _) => Task.Run<Link?>(async () =>
            {
                await driversBudget.CancelAsync();
                driversBudget.Token.ThrowIfCancellationRequested();
                return null; // Unreachable.
            }));

        var result = await sut.RedirectToOrigin("abc12345", CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, problem.StatusCode);
        dispatcher.Verify(
            x => x.DispatchAsync(It.IsAny<ClickTrackedEvent>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        clickCounter.Verify(
            x => x.RecordClickAsync(It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RedirectToOrigin_CallerDisconnectsDuringLookup_PropagatesCancellationInsteadOfReturning503()
    {
        // The caller's own token firing (not the budget) must be left to propagate untouched -
        // ASP.NET Core's own exception-handling middleware already special-cases a request-aborted
        // OperationCanceledException (no client left to send a 503 to), and catching it here would
        // instead try to write a now-pointless response. A long (not short) budget here is
        // deliberate - this proves the caller's own disconnect wins even while the budget itself is
        // nowhere near firing.
        await using var context = NewContext();
        var sut = NewController(context, out var cache, out _, out _);
        cache.Setup(x => x.GetOrFetch("abc12345", It.IsAny<Func<Task<Link?>>>(), It.IsAny<CancellationToken>()))
            .Returns<string, Func<Task<Link?>>, CancellationToken>((_, _, ct) => WaitForCancellationAsync(ct));
        using var callerCts = new CancellationTokenSource();
        await callerCts.CancelAsync();

        await Assert.ThrowsAsync<TaskCanceledException>(() => sut.RedirectToOrigin("abc12345", callerCts.Token));
    }

    [Fact]
    public async Task RedirectToOrigin_LinkFound_DispatchesAndRecordsWithTheCallersOwnTokenNotABudgetBoundOne()
    {
        // The click-tracking dispatch/counter calls must receive the exact token RedirectToOrigin
        // was called with (CancellationToken.None here), never a derived budget-linked token that
        // could cut them short - see the controller's own comment on why that side effect is
        // deliberately left unbounded by RequestBudget.
        await using var context = NewContext();
        var link = new Link("abc12345", "https://example.com/target", "abc12345", DateTime.UtcNow, null);
        var sut = NewController(context, out var cache, out var dispatcher, out var clickCounter);
        cache.Setup(x => x.GetOrFetch("abc12345", It.IsAny<Func<Task<Link?>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(link);

        await sut.RedirectToOrigin("abc12345", CancellationToken.None);

        dispatcher.Verify(
            x => x.DispatchAsync(It.IsAny<ClickTrackedEvent>(), Topics.ClickTracked, CancellationToken.None),
            Times.Once);
        clickCounter.Verify(
            x => x.RecordClickAsync("abc12345", null, It.IsAny<DateTime>(), CancellationToken.None),
            Times.Once);
    }
}
