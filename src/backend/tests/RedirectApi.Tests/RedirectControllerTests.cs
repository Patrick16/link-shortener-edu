using Common;
using Common.Models;
using Contracts.Events;
using Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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
        out Mock<IMessagePublisher> publisher,
        out Mock<IClickCounterService> clickCounter)
    {
        cache = new Mock<IEntityCacheService<Link>>();
        publisher = new Mock<IMessagePublisher>();
        clickCounter = new Mock<IClickCounterService>();

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("short.example");
        httpContext.Request.Path = "/abc12345";

        return new RedirectController(context, cache.Object, publisher.Object, clickCounter.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };
    }

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
        var sut = NewController(context, out var cache, out var publisher, out _);
        cache.Setup(x => x.GetOrFetch("abc12345", It.IsAny<Func<Task<Link?>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(link);

        await sut.RedirectToOrigin("abc12345", CancellationToken.None);

        publisher.Verify(
            x => x.PublishAsync(
                It.Is<ClickTrackedEvent>(e => e.Hash == "abc12345" && e.OutboundLink == "https://example.com/target" && e.Id != Guid.Empty),
                Topics.ClickTracked,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RedirectToOrigin_LinkFound_IncrementsRedisClickCounter()
    {
        await using var context = NewContext();
        var link = new Link("abc12345", "https://example.com/target", "abc12345", DateTime.UtcNow, null);
        var sut = NewController(context, out var cache, out _, out var clickCounter);
        cache.Setup(x => x.GetOrFetch("abc12345", It.IsAny<Func<Task<Link?>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(link);

        await sut.RedirectToOrigin("abc12345", CancellationToken.None);

        clickCounter.Verify(x => x.IncrementAsync("abc12345", It.IsAny<CancellationToken>()), Times.Once);
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
    public async Task RedirectToOrigin_LinkNotFound_ReturnsNotFoundAndDoesNotPublishOrIncrement()
    {
        await using var context = NewContext();
        var sut = NewController(context, out var cache, out var publisher, out var clickCounter);
        cache.Setup(x => x.GetOrFetch("missing", It.IsAny<Func<Task<Link?>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Link?)null);

        var result = await sut.RedirectToOrigin("missing", CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        publisher.Verify(
            x => x.PublishAsync(It.IsAny<ClickTrackedEvent>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        clickCounter.Verify(x => x.IncrementAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
