using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Common;
using Common.Models;
using Contracts.Events;
using Infrastructure;
using LinkApi;
using LinkApi.Controllers;
using LinkApi.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace LinkApi.Tests;

public class LinksControllerTests
{
    private static DatabaseContext NewContext()
    {
        var options = new DbContextOptionsBuilder<DatabaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new DatabaseContext(options);
    }

    private static LinksController NewController(
        DatabaseContext context,
        out Mock<IEntityCacheService<Link>> cache,
        out Mock<ILocalPublishQueue> publishQueue,
        out Mock<IHashGenerator> hashGenerator,
        ClaimsPrincipal? user = null)
    {
        cache = new Mock<IEntityCacheService<Link>>();
        publishQueue = new Mock<ILocalPublishQueue>();
        hashGenerator = new Mock<IHashGenerator>();

        var controller = new LinksController(context, cache.Object, publishQueue.Object, hashGenerator.Object, NullLogger<LinksController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = user ?? new ClaimsPrincipal(new ClaimsIdentity()) },
            },
        };
        return controller;
    }

    [Fact]
    public async Task CreateLink_AnonymousCaller_PublishesEventWithNullUserId()
    {
        await using var context = NewContext();
        var sut = NewController(context, out _, out var publishQueue, out var hashGenerator);
        hashGenerator.Setup(x => x.Generate("https://example.com")).Returns("abc12345");

        var result = await sut.CreateLink(
            new LinkCreateRequest { OriginalLink = "https://example.com" },
            CancellationToken.None);

        var response = Assert.IsType<LinkResponse>(result.Value);
        Assert.Equal("abc12345", response.ShortenLink);
        publishQueue.Verify(
            x => x.EnqueueAsync(
                It.Is<LinkCreatedEvent>(e => e.Hash == "abc12345" && e.OriginalLink == "https://example.com" && e.UserId == null),
                Topics.LinkCreated,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CreateLink_AuthenticatedCaller_PublishesEventWithUserId()
    {
        var userId = Guid.NewGuid();
        var identity = new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, userId.ToString())], "TestAuth");
        await using var context = NewContext();
        var sut = NewController(context, out _, out var publishQueue, out var hashGenerator, new ClaimsPrincipal(identity));
        hashGenerator.Setup(x => x.Generate(It.IsAny<string>())).Returns("abc12345");

        await sut.CreateLink(new LinkCreateRequest { OriginalLink = "https://example.com" }, CancellationToken.None);

        publishQueue.Verify(
            x => x.EnqueueAsync(It.Is<LinkCreatedEvent>(e => e.UserId == userId), Topics.LinkCreated, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetLink_CacheHit_ReturnsCachedValueWithoutTouchingDb()
    {
        await using var context = NewContext();
        var cachedLink = new Link("abc12345", "https://example.com", "abc12345", DateTime.UtcNow, null);
        var sut = NewController(context, out var cache, out _, out _);
        cache.Setup(x => x.GetOrFetch("abc12345", It.IsAny<Func<Task<Link?>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(cachedLink);

        var result = await sut.GetLink("abc12345", CancellationToken.None);

        var response = Assert.IsType<LinkResponse>(result.Value);
        Assert.Equal("abc12345", response.ShortenLink);
    }

    [Fact]
    public async Task GetLink_CacheMiss_FetchesFromDatabase()
    {
        await using var context = NewContext();
        var stored = new Link("abc12345", "https://example.com", "abc12345", DateTime.UtcNow, null);
        context.Links.Add(stored);
        await context.SaveChangesAsync();
        var sut = NewController(context, out var cache, out _, out _);
        cache.Setup(x => x.GetOrFetch("abc12345", It.IsAny<Func<Task<Link?>>>(), It.IsAny<CancellationToken>()))
            .Returns<string, Func<Task<Link?>>, CancellationToken>((_, fetch, _) => fetch());

        var result = await sut.GetLink("abc12345", CancellationToken.None);

        var response = Assert.IsType<LinkResponse>(result.Value);
        Assert.Equal("abc12345", response.ShortenLink);
    }

    [Fact]
    public async Task GetLink_NotFoundAnywhere_ReturnsNotFound()
    {
        await using var context = NewContext();
        var sut = NewController(context, out var cache, out _, out _);
        cache.Setup(x => x.GetOrFetch("missing", It.IsAny<Func<Task<Link?>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Link?)null);

        var result = await sut.GetLink("missing", CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task GetLinks_AuthenticatedCaller_ReturnsOnlyOwnLinks()
    {
        var userId = Guid.NewGuid();
        await using var context = NewContext();
        context.Links.Add(new Link("aaa11111", "https://a.example", "aaa11111", DateTime.UtcNow, userId, ClickCount: 3));
        context.Links.Add(new Link("bbb22222", "https://b.example", "bbb22222", DateTime.UtcNow, Guid.NewGuid()));
        context.Links.Add(new Link("ccc33333", "https://c.example", "ccc33333", DateTime.UtcNow, null));
        await context.SaveChangesAsync();
        var identity = new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, userId.ToString())], "TestAuth");
        var sut = NewController(context, out _, out _, out _, new ClaimsPrincipal(identity));

        var result = await sut.GetLinks(page: 1, CancellationToken.None);

        var response = Assert.IsType<LinksPageResponse>(result.Value);
        Assert.Equal(1, response.TotalCount);
        var item = Assert.Single(response.Items);
        Assert.Equal("aaa11111", item.ShortenLink);
        Assert.Equal(3, item.ClickCount);
    }

    [Fact]
    public async Task GetLinks_InternalApiKeyCaller_ReturnsEveryUsersLinksUnfiltered()
    {
        // The InternalApiKey scheme grants control-api's data-pool preload unscoped access - a
        // broad sample across every user's links, since a per-user JWT could never express that.
        // The controller distinguishes it from a normal JWT-authenticated caller purely by the
        // Constants.InternalClaim claim, which InternalApiKeyAuthenticationHandler is what actually
        // attaches in production - this test drives the controller with that same claim directly,
        // the same way the JWT-authenticated tests below drive it with a Sub claim.
        await using var context = NewContext();
        context.Links.Add(new Link("aaa11111", "https://a.example", "aaa11111", DateTime.UtcNow, Guid.NewGuid()));
        context.Links.Add(new Link("bbb22222", "https://b.example", "bbb22222", DateTime.UtcNow, Guid.NewGuid()));
        context.Links.Add(new Link("ccc33333", "https://c.example", "ccc33333", DateTime.UtcNow, null));
        await context.SaveChangesAsync();
        var identity = new ClaimsIdentity([new Claim(Constants.InternalClaim, "true")], "InternalApiKey");
        var sut = NewController(context, out _, out _, out _, new ClaimsPrincipal(identity));

        var result = await sut.GetLinks(page: 1, CancellationToken.None);

        var response = Assert.IsType<LinksPageResponse>(result.Value);
        Assert.Equal(3, response.TotalCount);
    }

    [Fact]
    public async Task GetLinks_SecondPage_SkipsFirstPageSize()
    {
        var userId = Guid.NewGuid();
        await using var context = NewContext();
        for (var i = 0; i < 60; i++)
        {
            var hash = $"h{i:D7}";
            context.Links.Add(new Link(hash, $"https://example.com/{i}", hash, DateTime.UtcNow.AddSeconds(i), userId));
        }
        await context.SaveChangesAsync();
        var identity = new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, userId.ToString())], "TestAuth");
        var sut = NewController(context, out _, out _, out _, new ClaimsPrincipal(identity));

        var result = await sut.GetLinks(page: 2, CancellationToken.None);

        var response = Assert.IsType<LinksPageResponse>(result.Value);
        Assert.Equal(60, response.TotalCount);
        Assert.Equal(2, response.TotalPages);
        Assert.Equal(10, response.Items.Count);
    }

    [Fact]
    public async Task GetLinks_PageFarBeyondTheEnd_ReturnsEmptyItemsInsteadOfOverflowing()
    {
        // Regression: (page - 1) * PageSize in plain int arithmetic overflows to negative well
        // before int.MaxValue (~43M at PageSize=50) - Postgres then rejects the resulting OFFSET,
        // so a page number past the end used to 500 instead of coming back as an empty page.
        var userId = Guid.NewGuid();
        await using var context = NewContext();
        context.Links.Add(new Link("aaa11111", "https://a.example", "aaa11111", DateTime.UtcNow, userId));
        await context.SaveChangesAsync();
        var identity = new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, userId.ToString())], "TestAuth");
        var sut = NewController(context, out _, out _, out _, new ClaimsPrincipal(identity));

        var result = await sut.GetLinks(page: 99_999_999, CancellationToken.None);

        var response = Assert.IsType<LinksPageResponse>(result.Value);
        Assert.Equal(1, response.TotalCount);
        Assert.Empty(response.Items);
    }

    [Fact]
    public async Task GetLinks_LinksShareTheSameCreatedAt_OrderingIsStableAcrossPages()
    {
        // Regression: ordering by CreatedAt alone gives SQL no tie-breaker for equal timestamps, so
        // a row could be duplicated across two pages or skipped when paging through results. Every
        // link here shares the exact same CreatedAt to force the tie; Hash as a secondary sort key
        // makes page 1 + page 2 partition the set with no overlap and no gap regardless of it.
        var userId = Guid.NewGuid();
        await using var context = NewContext();
        var createdAt = DateTime.UtcNow;
        var hashes = Enumerable.Range(0, 60).Select(i => $"h{i:D7}").ToList();
        foreach (var hash in hashes)
        {
            context.Links.Add(new Link(hash, $"https://example.com/{hash}", hash, createdAt, userId));
        }
        await context.SaveChangesAsync();
        var identity = new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, userId.ToString())], "TestAuth");
        var sut = NewController(context, out _, out _, out _, new ClaimsPrincipal(identity));

        var page1 = Assert.IsType<LinksPageResponse>((await sut.GetLinks(page: 1, CancellationToken.None)).Value);
        var page2 = Assert.IsType<LinksPageResponse>((await sut.GetLinks(page: 2, CancellationToken.None)).Value);

        var page1Hashes = page1.Items.Select(i => i.ShortenLink).ToList();
        var page2Hashes = page2.Items.Select(i => i.ShortenLink).ToList();
        Assert.Equal(50, page1Hashes.Count);
        Assert.Equal(10, page2Hashes.Count);
        Assert.Empty(page1Hashes.Intersect(page2Hashes));
        Assert.Equal(hashes.Count, page1Hashes.Concat(page2Hashes).Distinct().Count());
    }

    [Fact]
    public async Task GetLinks_InvalidPage_ReturnsProblem()
    {
        await using var context = NewContext();
        var sut = NewController(context, out _, out _, out _);

        var result = await sut.GetLinks(page: 0, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);
    }
}
