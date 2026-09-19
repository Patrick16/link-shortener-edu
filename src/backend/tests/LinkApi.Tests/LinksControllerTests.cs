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
        out Mock<IMessagePublisher> publisher,
        out Mock<IHashGenerator> hashGenerator,
        ClaimsPrincipal? user = null)
    {
        cache = new Mock<IEntityCacheService<Link>>();
        publisher = new Mock<IMessagePublisher>();
        hashGenerator = new Mock<IHashGenerator>();

        var controller = new LinksController(context, cache.Object, publisher.Object, hashGenerator.Object)
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
        var sut = NewController(context, out _, out var publisher, out var hashGenerator);
        hashGenerator.Setup(x => x.Generate("https://example.com")).Returns("abc12345");

        var result = await sut.CreateLink(
            new LinkCreateRequest { OriginalLink = "https://example.com" },
            CancellationToken.None);

        var response = Assert.IsType<LinkResponse>(result.Value);
        Assert.Equal("abc12345", response.ShortenLink);
        publisher.Verify(
            x => x.PublishAsync(
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
        var sut = NewController(context, out _, out var publisher, out var hashGenerator, new ClaimsPrincipal(identity));
        hashGenerator.Setup(x => x.Generate(It.IsAny<string>())).Returns("abc12345");

        await sut.CreateLink(new LinkCreateRequest { OriginalLink = "https://example.com" }, CancellationToken.None);

        publisher.Verify(
            x => x.PublishAsync(It.Is<LinkCreatedEvent>(e => e.UserId == userId), Topics.LinkCreated, It.IsAny<CancellationToken>()),
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
    public async Task GetLinks_AnonymousCaller_ReturnsEveryUsersLinks()
    {
        await using var context = NewContext();
        context.Links.Add(new Link("aaa11111", "https://a.example", "aaa11111", DateTime.UtcNow, Guid.NewGuid()));
        context.Links.Add(new Link("bbb22222", "https://b.example", "bbb22222", DateTime.UtcNow, null));
        await context.SaveChangesAsync();
        var sut = NewController(context, out _, out _, out _);

        var result = await sut.GetLinks(page: 1, CancellationToken.None);

        var response = Assert.IsType<LinksPageResponse>(result.Value);
        Assert.Equal(2, response.TotalCount);
        Assert.Equal(2, response.Items.Count);
    }

    [Fact]
    public async Task GetLinks_AuthenticatedCaller_ReturnsOnlyOwnLinks()
    {
        var userId = Guid.NewGuid();
        await using var context = NewContext();
        context.Links.Add(new Link("aaa11111", "https://a.example", "aaa11111", DateTime.UtcNow, userId));
        context.Links.Add(new Link("bbb22222", "https://b.example", "bbb22222", DateTime.UtcNow, Guid.NewGuid()));
        context.Links.Add(new Link("ccc33333", "https://c.example", "ccc33333", DateTime.UtcNow, null));
        await context.SaveChangesAsync();
        var identity = new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, userId.ToString())], "TestAuth");
        var sut = NewController(context, out _, out _, out _, new ClaimsPrincipal(identity));

        var result = await sut.GetLinks(page: 1, CancellationToken.None);

        var response = Assert.IsType<LinksPageResponse>(result.Value);
        Assert.Equal(1, response.TotalCount);
        Assert.Equal("aaa11111", Assert.Single(response.Items).ShortenLink);
    }

    [Fact]
    public async Task GetLinks_SecondPage_SkipsFirstPageSize()
    {
        await using var context = NewContext();
        for (var i = 0; i < 60; i++)
        {
            var hash = $"h{i:D7}";
            context.Links.Add(new Link(hash, $"https://example.com/{i}", hash, DateTime.UtcNow.AddSeconds(i), null));
        }
        await context.SaveChangesAsync();
        var sut = NewController(context, out _, out _, out _);

        var result = await sut.GetLinks(page: 2, CancellationToken.None);

        var response = Assert.IsType<LinksPageResponse>(result.Value);
        Assert.Equal(60, response.TotalCount);
        Assert.Equal(2, response.TotalPages);
        Assert.Equal(10, response.Items.Count);
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
