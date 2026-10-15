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
        out Mock<IEventDispatcher> dispatcher,
        out Mock<IHashGenerator> hashGenerator,
        out Mock<ICustomAliasCreator> aliasCreator,
        out Mock<ILinkDeletionMarkerStore> deletionMarkers,
        ClaimsPrincipal? user = null)
    {
        cache = new Mock<IEntityCacheService<Link>>();
        dispatcher = new Mock<IEventDispatcher>();
        hashGenerator = new Mock<IHashGenerator>();
        aliasCreator = new Mock<ICustomAliasCreator>();
        deletionMarkers = new Mock<ILinkDeletionMarkerStore>();

        var controller = new LinksController(context, cache.Object, dispatcher.Object, hashGenerator.Object, aliasCreator.Object, deletionMarkers.Object, NullLogger<LinksController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = user ?? new ClaimsPrincipal(new ClaimsIdentity()) },
            },
        };
        return controller;
    }

    private static LinksController NewController(
        DatabaseContext context,
        out Mock<IEntityCacheService<Link>> cache,
        out Mock<IEventDispatcher> dispatcher,
        out Mock<IHashGenerator> hashGenerator,
        out Mock<ICustomAliasCreator> aliasCreator,
        ClaimsPrincipal? user = null) =>
        NewController(context, out cache, out dispatcher, out hashGenerator, out aliasCreator, out _, user);

    [Fact]
    public async Task CreateLink_AnonymousCaller_PublishesEventWithNullUserId()
    {
        await using var context = NewContext();
        var sut = NewController(context, out _, out var dispatcher, out var hashGenerator, out _);
        hashGenerator.Setup(x => x.Generate("https://example.com")).Returns("abc12345");

        var result = await sut.CreateLink(
            new LinkCreateRequest { OriginalLink = "https://example.com" },
            CancellationToken.None);

        var response = Assert.IsType<LinkResponse>(result.Value);
        Assert.Equal("abc12345", response.ShortenLink);
        dispatcher.Verify(
            x => x.DispatchAsync(
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
        var sut = NewController(context, out _, out var dispatcher, out var hashGenerator, out _, new ClaimsPrincipal(identity));
        hashGenerator.Setup(x => x.Generate(It.IsAny<string>())).Returns("abc12345");

        await sut.CreateLink(new LinkCreateRequest { OriginalLink = "https://example.com" }, CancellationToken.None);

        dispatcher.Verify(
            x => x.DispatchAsync(It.Is<LinkCreatedEvent>(e => e.UserId == userId), Topics.LinkCreated, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CreateLink_Success_WritesOptimisticCacheEntryWithShortTtl()
    {
        // Closes the PgCat read-your-writes race (sandbox/docs/scenarios/pitfalls/
        // pgcat-read-your-writes-race.md): RedirectApi is cache-first, so this is what lets an
        // immediate redirect right after create resolve without ever touching Postgres.
        await using var context = NewContext();
        var sut = NewController(context, out var cache, out _, out var hashGenerator, out _);
        hashGenerator.Setup(x => x.Generate("https://example.com")).Returns("abc12345");

        await sut.CreateLink(new LinkCreateRequest { OriginalLink = "https://example.com" }, CancellationToken.None);

        cache.Verify(
            x => x.CacheAsync(
                It.Is<Link>(l => l.Hash == "abc12345" && l.OriginalLink == "https://example.com" && l.ShortenLink == "abc12345"),
                "abc12345",
                It.IsAny<CancellationToken>(),
                60),
            Times.Once);
    }

    [Fact]
    public async Task CreateLink_DispatchThrows_NeverCaches()
    {
        // Ordering matters: in sync/gRPC mode a failed dispatch means the link was never created at
        // all, and the request fails outright - caching first would leave a ghost entry for a hash
        // that will never resolve anywhere.
        await using var context = NewContext();
        var sut = NewController(context, out var cache, out var dispatcher, out var hashGenerator, out _);
        hashGenerator.Setup(x => x.Generate(It.IsAny<string>())).Returns("abc12345");
        dispatcher
            .Setup(x => x.DispatchAsync(It.IsAny<LinkCreatedEvent>(), Topics.LinkCreated, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("downstream unreachable"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.CreateLink(new LinkCreateRequest { OriginalLink = "https://example.com" }, CancellationToken.None));

        cache.Verify(x => x.CacheAsync(It.IsAny<Link>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task CreateLink_EmptyOriginalLink_ReturnsBadRequestWithoutPublishing()
    {
        await using var context = NewContext();
        var sut = NewController(context, out _, out var dispatcher, out var hashGenerator, out _);

        var result = await sut.CreateLink(new LinkCreateRequest { OriginalLink = "" }, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);
        hashGenerator.Verify(x => x.Generate(It.IsAny<string>()), Times.Never);
        dispatcher.Verify(
            x => x.DispatchAsync(It.IsAny<LinkCreatedEvent>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("not-a-url")]
    [InlineData("ftp://example.com/file")]
    public async Task CreateLink_NonHttpOriginalLink_ReturnsBadRequestWithoutPublishing(string originalLink)
    {
        // Regression: without this check, a non-http(s) value is persisted as-is, and RedirectApi
        // later calls Redirect(link.OriginalLink) on it verbatim - an open redirect out of a trusted
        // short-link domain to an arbitrary scheme.
        await using var context = NewContext();
        var sut = NewController(context, out _, out var dispatcher, out _, out _);

        var result = await sut.CreateLink(new LinkCreateRequest { OriginalLink = originalLink }, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);
        dispatcher.Verify(
            x => x.DispatchAsync(It.IsAny<LinkCreatedEvent>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetLink_CacheHit_ReturnsCachedValueWithoutTouchingDb()
    {
        await using var context = NewContext();
        var cachedLink = new Link("abc12345", "https://example.com", "abc12345", DateTime.UtcNow, null);
        var sut = NewController(context, out var cache, out _, out _, out _);
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
        var sut = NewController(context, out var cache, out _, out _, out _);
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
        var sut = NewController(context, out var cache, out _, out _, out _);
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
        var sut = NewController(context, out _, out _, out _, out _, new ClaimsPrincipal(identity));

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
        var sut = NewController(context, out _, out _, out _, out _, new ClaimsPrincipal(identity));

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
        var sut = NewController(context, out _, out _, out _, out _, new ClaimsPrincipal(identity));

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
        var sut = NewController(context, out _, out _, out _, out _, new ClaimsPrincipal(identity));

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
        var sut = NewController(context, out _, out _, out _, out _, new ClaimsPrincipal(identity));

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
        var sut = NewController(context, out _, out _, out _, out _);

        var result = await sut.GetLinks(page: 0, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);
    }

    [Fact]
    public async Task CreateLink_AliasAnonymousCaller_ReturnsUnauthorizedWithoutCallingAliasCreator()
    {
        await using var context = NewContext();
        var sut = NewController(context, out _, out _, out _, out var aliasCreator);

        var result = await sut.CreateLink(new LinkCreateRequest { OriginalLink = "https://example.com", Alias = "my-link" }, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status401Unauthorized, objectResult.StatusCode);
        aliasCreator.Verify(
            x => x.CreateAsync(It.IsAny<LinkCreatedEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CreateLink_AliasAuthenticatedAndFree_ReturnsCreatedAndCachesWithoutExplicitTtl()
    {
        var userId = Guid.NewGuid();
        var identity = new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, userId.ToString())], "TestAuth");
        await using var context = NewContext();
        var sut = NewController(context, out var cache, out _, out _, out var aliasCreator, new ClaimsPrincipal(identity));
        aliasCreator.Setup(x => x.CreateAsync(It.IsAny<LinkCreatedEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AliasCreationResult.Created);

        var result = await sut.CreateLink(new LinkCreateRequest { OriginalLink = "https://example.com", Alias = "My-Link" }, CancellationToken.None);

        var response = Assert.IsType<LinkResponse>(result.Value);
        // Normalized to lowercase - "My-Link" and "my-link" must be the same slot.
        Assert.Equal("my-link", response.ShortenLink);
        aliasCreator.Verify(
            x => x.CreateAsync(
                It.Is<LinkCreatedEvent>(e => e.Hash == "my-link" && e.HasCustomAlias && e.UserId == userId),
                It.IsAny<CancellationToken>()),
            Times.Once);
        // No explicit TTL arg - the 4-arg overload with ttlSeconds is the optimistic/short-lived one
        // the plain-hash path uses; this must be the no-explicit-TTL (full-duration default) call,
        // since the row is already durably persisted by the time CreateAsync returns Created.
        cache.Verify(
            x => x.CacheAsync(It.Is<Link>(l => l.Hash == "my-link" && l.HasCustomAlias), "my-link", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CreateLink_AliasAlreadyTaken_ReturnsConflictWithoutCaching()
    {
        var identity = new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString())], "TestAuth");
        await using var context = NewContext();
        var sut = NewController(context, out var cache, out _, out _, out var aliasCreator, new ClaimsPrincipal(identity));
        aliasCreator.Setup(x => x.CreateAsync(It.IsAny<LinkCreatedEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AliasCreationResult.AliasTaken);

        var result = await sut.CreateLink(new LinkCreateRequest { OriginalLink = "https://example.com", Alias = "taken" }, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status409Conflict, objectResult.StatusCode);
        cache.Verify(x => x.CacheAsync(It.IsAny<Link>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("ab")] // below minimum length
    [InlineData("-leading-dash")]
    [InlineData("has space")]
    [InlineData("has/slash")]
    public async Task CreateLink_AliasInvalidFormat_ReturnsBadRequestWithoutCallingAliasCreator(string alias)
    {
        var identity = new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString())], "TestAuth");
        await using var context = NewContext();
        var sut = NewController(context, out _, out _, out _, out var aliasCreator, new ClaimsPrincipal(identity));

        var result = await sut.CreateLink(new LinkCreateRequest { OriginalLink = "https://example.com", Alias = alias }, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);
        aliasCreator.Verify(
            x => x.CreateAsync(It.IsAny<LinkCreatedEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CreateLink_AliasIsReservedWord_ReturnsBadRequestWithoutCallingAliasCreator()
    {
        var identity = new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString())], "TestAuth");
        await using var context = NewContext();
        var sut = NewController(context, out _, out _, out _, out var aliasCreator, new ClaimsPrincipal(identity));

        var result = await sut.CreateLink(new LinkCreateRequest { OriginalLink = "https://example.com", Alias = "scalar" }, CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);
        aliasCreator.Verify(
            x => x.CreateAsync(It.IsAny<LinkCreatedEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CreateLink_NoAlias_PublishesEventWithHasCustomAliasFalse()
    {
        // Regression: the plain (non-aliased) path must keep going through the standing
        // IEventDispatcher/Messaging:Mode toggle, unaffected by the alias feature.
        await using var context = NewContext();
        var sut = NewController(context, out _, out var dispatcher, out var hashGenerator, out var aliasCreator);
        hashGenerator.Setup(x => x.Generate(It.IsAny<string>())).Returns("abc12345");

        await sut.CreateLink(new LinkCreateRequest { OriginalLink = "https://example.com" }, CancellationToken.None);

        dispatcher.Verify(
            x => x.DispatchAsync(It.Is<LinkCreatedEvent>(e => !e.HasCustomAlias), Topics.LinkCreated, It.IsAny<CancellationToken>()),
            Times.Once);
        aliasCreator.Verify(
            x => x.CreateAsync(It.IsAny<LinkCreatedEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task CreateLink_NonPositiveTtlSeconds_ReturnsBadRequestWithoutPublishing(int ttlSeconds)
    {
        await using var context = NewContext();
        var sut = NewController(context, out _, out var dispatcher, out _, out _);

        var result = await sut.CreateLink(
            new LinkCreateRequest { OriginalLink = "https://example.com", TtlSeconds = ttlSeconds },
            CancellationToken.None);

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, objectResult.StatusCode);
        dispatcher.Verify(
            x => x.DispatchAsync(It.IsAny<LinkCreatedEvent>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CreateLink_WithTtlSeconds_PublishesEventWithExpiresAtSetAndCachesIt()
    {
        await using var context = NewContext();
        var sut = NewController(context, out var cache, out var dispatcher, out var hashGenerator, out _);
        hashGenerator.Setup(x => x.Generate(It.IsAny<string>())).Returns("abc12345");
        var before = DateTime.UtcNow;

        await sut.CreateLink(
            new LinkCreateRequest { OriginalLink = "https://example.com", TtlSeconds = 60 },
            CancellationToken.None);

        var after = DateTime.UtcNow;
        dispatcher.Verify(
            x => x.DispatchAsync(
                It.Is<LinkCreatedEvent>(e => e.ExpiresAt != null && e.ExpiresAt >= before.AddSeconds(60) && e.ExpiresAt <= after.AddSeconds(60)),
                Topics.LinkCreated,
                It.IsAny<CancellationToken>()),
            Times.Once);
        cache.Verify(
            x => x.CacheAsync(It.Is<Link>(l => l.ExpiresAt != null), "abc12345", It.IsAny<CancellationToken>(), 60),
            Times.Once);
    }

    [Fact]
    public async Task GetLinks_ExcludesDeletedAndExpiredLinksByDefault()
    {
        var userId = Guid.NewGuid();
        await using var context = NewContext();
        context.Links.Add(new Link("live0001", "https://a.example", "live0001", DateTime.UtcNow, userId));
        context.Links.Add(new Link("del00001", "https://b.example", "del00001", DateTime.UtcNow, userId, DeletedAt: DateTime.UtcNow));
        context.Links.Add(new Link("exp00001", "https://c.example", "exp00001", DateTime.UtcNow, userId, ExpiresAt: DateTime.UtcNow.AddMinutes(-1)));
        context.Links.Add(new Link("fut00001", "https://d.example", "fut00001", DateTime.UtcNow, userId, ExpiresAt: DateTime.UtcNow.AddHours(1)));
        await context.SaveChangesAsync();
        var identity = new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, userId.ToString())], "TestAuth");
        var sut = NewController(context, out _, out _, out _, out _, new ClaimsPrincipal(identity));

        var result = await sut.GetLinks(page: 1, CancellationToken.None);

        var response = Assert.IsType<LinksPageResponse>(result.Value);
        var hashes = response.Items.Select(i => i.ShortenLink).ToList();
        Assert.Equal(2, response.TotalCount);
        Assert.Contains("live0001", hashes);
        Assert.Contains("fut00001", hashes);
    }

    [Fact]
    public async Task GetLinks_DeletedTrue_ReturnsOnlyDeletedAndExpiredLinks()
    {
        var userId = Guid.NewGuid();
        await using var context = NewContext();
        context.Links.Add(new Link("live0001", "https://a.example", "live0001", DateTime.UtcNow, userId));
        context.Links.Add(new Link("del00001", "https://b.example", "del00001", DateTime.UtcNow, userId, DeletedAt: DateTime.UtcNow));
        context.Links.Add(new Link("exp00001", "https://c.example", "exp00001", DateTime.UtcNow, userId, ExpiresAt: DateTime.UtcNow.AddMinutes(-1)));
        context.Links.Add(new Link("fut00001", "https://d.example", "fut00001", DateTime.UtcNow, userId, ExpiresAt: DateTime.UtcNow.AddHours(1)));
        await context.SaveChangesAsync();
        var identity = new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, userId.ToString())], "TestAuth");
        var sut = NewController(context, out _, out _, out _, out _, new ClaimsPrincipal(identity));

        var result = await sut.GetLinks(page: 1, CancellationToken.None, deleted: true);

        var response = Assert.IsType<LinksPageResponse>(result.Value);
        var hashes = response.Items.Select(i => i.ShortenLink).ToList();
        Assert.Equal(2, response.TotalCount);
        Assert.Contains("del00001", hashes);
        Assert.Contains("exp00001", hashes);
    }

    [Fact]
    public async Task DeleteLink_HashDoesNotExist_ReturnsNotFound()
    {
        var identity = new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString())], "TestAuth");
        await using var context = NewContext();
        var sut = NewController(context, out _, out _, out _, out _, out _, new ClaimsPrincipal(identity));

        var result = await sut.DeleteLink("missing", CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task DeleteLink_NotTheOwner_ReturnsForbidWithoutPublishing()
    {
        var ownerId = Guid.NewGuid();
        var callerId = Guid.NewGuid();
        await using var context = NewContext();
        context.Links.Add(new Link("abc12345", "https://example.com", "abc12345", DateTime.UtcNow, ownerId));
        await context.SaveChangesAsync();
        var identity = new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, callerId.ToString())], "TestAuth");
        var sut = NewController(context, out _, out var dispatcher, out _, out _, out var deletionMarkers, new ClaimsPrincipal(identity));

        var result = await sut.DeleteLink("abc12345", CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
        dispatcher.Verify(x => x.DispatchAsync(It.IsAny<LinkDeletedEvent>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        deletionMarkers.Verify(x => x.TryMarkOptimisticallyAsync(It.IsAny<string>(), It.IsAny<DeletedReason>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteLink_AnonymousLink_ReturnsForbid()
    {
        // UserId == null can never match a caller's own Guid - anonymous links aren't deletable by anyone.
        var callerId = Guid.NewGuid();
        await using var context = NewContext();
        context.Links.Add(new Link("abc12345", "https://example.com", "abc12345", DateTime.UtcNow, null));
        await context.SaveChangesAsync();
        var identity = new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, callerId.ToString())], "TestAuth");
        var sut = NewController(context, out _, out _, out _, out _, out _, new ClaimsPrincipal(identity));

        var result = await sut.DeleteLink("abc12345", CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task DeleteLink_AlreadyDeleted_ReturnsNoContentWithoutPublishingAgain()
    {
        var userId = Guid.NewGuid();
        await using var context = NewContext();
        context.Links.Add(new Link("abc12345", "https://example.com", "abc12345", DateTime.UtcNow, userId, DeletedAt: DateTime.UtcNow));
        await context.SaveChangesAsync();
        var identity = new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, userId.ToString())], "TestAuth");
        var sut = NewController(context, out _, out var dispatcher, out _, out _, out var deletionMarkers, new ClaimsPrincipal(identity));

        var result = await sut.DeleteLink("abc12345", CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        dispatcher.Verify(x => x.DispatchAsync(It.IsAny<LinkDeletedEvent>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        deletionMarkers.Verify(x => x.TryMarkOptimisticallyAsync(It.IsAny<string>(), It.IsAny<DeletedReason>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DeleteLink_Owner_MarksOptimisticallyAndPublishesDeletedEvent()
    {
        var userId = Guid.NewGuid();
        await using var context = NewContext();
        context.Links.Add(new Link("abc12345", "https://example.com", "abc12345", DateTime.UtcNow, userId));
        await context.SaveChangesAsync();
        var identity = new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, userId.ToString())], "TestAuth");
        var sut = NewController(context, out _, out var dispatcher, out _, out _, out var deletionMarkers, new ClaimsPrincipal(identity));

        var result = await sut.DeleteLink("abc12345", CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        deletionMarkers.Verify(x => x.TryMarkOptimisticallyAsync("abc12345", DeletedReason.Manual, It.IsAny<CancellationToken>()), Times.Once);
        dispatcher.Verify(
            x => x.DispatchAsync(It.Is<LinkDeletedEvent>(e => e.Hash == "abc12345"), Topics.LinkDeleted, It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
