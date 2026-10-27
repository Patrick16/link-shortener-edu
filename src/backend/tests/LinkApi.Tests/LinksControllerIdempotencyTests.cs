using System.Security.Claims;
using System.Text.Json;
using Common;
using Common.Models;
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

// Controller-level orchestration (claim/replay/evict) only - RedisIdempotencyStore itself has no
// dedicated test file, same as RedisLinkDeletionMarkerStore it's deliberately modeled on (both are
// thin wrappers over real StackExchange.Redis calls this codebase doesn't unit-test directly,
// trusting live verification instead - see .notes/feature-docs/ for this feature's own pass).
internal sealed class FakeIdempotencyStore : IIdempotencyStore
{
    private readonly Dictionary<string, IdempotencyRecord> _records = [];

    public void SeedPending(string key) => _records[key] = new IdempotencyRecord(IdempotencyStatus.Pending);

    public bool Contains(string key) => _records.ContainsKey(key);

    public Task<bool> TryClaimAsync(string key, CancellationToken cancellationToken)
    {
        if (_records.ContainsKey(key)) return Task.FromResult(false);
        _records[key] = new IdempotencyRecord(IdempotencyStatus.Pending);
        return Task.FromResult(true);
    }

    public Task<IdempotencyRecord?> GetAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult(_records.TryGetValue(key, out var record) ? record : null);

    public Task CompleteAsync(string key, int statusCode, string contentType, string bodyJson, CancellationToken cancellationToken)
    {
        _records[key] = new IdempotencyRecord(IdempotencyStatus.Completed, statusCode, contentType, bodyJson);
        return Task.CompletedTask;
    }

    public Task EvictAsync(string key, CancellationToken cancellationToken)
    {
        _records.Remove(key);
        return Task.CompletedTask;
    }
}

public class LinksControllerIdempotencyTests
{
    private static DatabaseContext NewContext()
    {
        var options = new DbContextOptionsBuilder<DatabaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new DatabaseContext(options);
    }

    // A replayed response comes back through ActionResult<T>.Result (an ObjectResult wrapping a
    // generic JsonElement, see LinksController.ExtractForReplay), not .Value - ActionResult<T>'s
    // own duality means only one of the two is ever populated. This normalizes both shapes so a
    // test can compare "first call's hash" against "replayed call's hash" uniformly.
    private static string ExtractShortenLink(ActionResult<LinkResponse> result)
    {
        if (result.Value is { } value) return value.ShortenLink;

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        var json = Assert.IsType<JsonElement>(objectResult.Value);
        return json.GetProperty("shortenLink").GetString()!;
    }

    private static LinksController NewController(
        DatabaseContext context, FakeIdempotencyStore idempotencyStore, out Mock<IHashGenerator> hashGenerator)
    {
        hashGenerator = new Mock<IHashGenerator>();
        return new LinksController(
            context,
            Mock.Of<IEntityCacheService<Link>>(),
            Mock.Of<IEventDispatcher>(),
            hashGenerator.Object,
            Mock.Of<ICustomAliasCreator>(),
            Mock.Of<ILinkDeletionMarkerStore>(),
            idempotencyStore,
            NullLogger<LinksController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) },
            },
        };
    }

    [Fact]
    public async Task CreateLink_SameIdempotencyKeyTwice_ReplaysTheFirstResponseWithoutGeneratingASecondHash()
    {
        await using var context = NewContext();
        var idempotencyStore = new FakeIdempotencyStore();
        var sut = NewController(context, idempotencyStore, out var hashGenerator);
        var callCount = 0;
        hashGenerator.Setup(x => x.Generate(It.IsAny<string>())).Returns(() => $"hash{Interlocked.Increment(ref callCount)}");
        var request = new LinkCreateRequest { OriginalLink = "https://example.com" };

        var first = await sut.CreateLink(request, CancellationToken.None, "retry-key-1");
        var second = await sut.CreateLink(request, CancellationToken.None, "retry-key-1");

        Assert.Equal(1, callCount);
        Assert.Equal(ExtractShortenLink(first), ExtractShortenLink(second));
    }

    [Fact]
    public async Task CreateLink_DifferentIdempotencyKeys_CreatesTwoIndependentLinks()
    {
        await using var context = NewContext();
        var idempotencyStore = new FakeIdempotencyStore();
        var sut = NewController(context, idempotencyStore, out var hashGenerator);
        var callCount = 0;
        hashGenerator.Setup(x => x.Generate(It.IsAny<string>())).Returns(() => $"hash{Interlocked.Increment(ref callCount)}");
        var request = new LinkCreateRequest { OriginalLink = "https://example.com" };

        await sut.CreateLink(request, CancellationToken.None, "key-a");
        await sut.CreateLink(request, CancellationToken.None, "key-b");

        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task CreateLink_NoIdempotencyKey_EachCallGeneratesIndependently()
    {
        // Pre-existing at-least-once behavior must survive unchanged when the header is absent.
        await using var context = NewContext();
        var idempotencyStore = new FakeIdempotencyStore();
        var sut = NewController(context, idempotencyStore, out var hashGenerator);
        var callCount = 0;
        hashGenerator.Setup(x => x.Generate(It.IsAny<string>())).Returns(() => $"hash{Interlocked.Increment(ref callCount)}");
        var request = new LinkCreateRequest { OriginalLink = "https://example.com" };

        await sut.CreateLink(request, CancellationToken.None);
        await sut.CreateLink(request, CancellationToken.None);

        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task CreateLink_IdempotencyKeyAlreadyPending_Returns409WithoutGeneratingAHash()
    {
        await using var context = NewContext();
        var idempotencyStore = new FakeIdempotencyStore();
        idempotencyStore.SeedPending("in-flight-key");
        var sut = NewController(context, idempotencyStore, out var hashGenerator);
        var request = new LinkCreateRequest { OriginalLink = "https://example.com" };

        var result = await sut.CreateLink(request, CancellationToken.None, "in-flight-key");

        var objectResult = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status409Conflict, objectResult.StatusCode);
        hashGenerator.Verify(x => x.Generate(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task CreateLink_SameIdempotencyKeyAfterAValidationFailure_ReplaysTheSame400()
    {
        // Any outcome CreateLinkCoreAsync produces - including a Problem() result, not just success
        // - is cached and replayed. A malformed-URL request fails validation before ever reaching
        // the hash generator; the retry below never calls it either, proving the 400 was replayed,
        // not re-validated.
        await using var context = NewContext();
        var idempotencyStore = new FakeIdempotencyStore();
        var sut = NewController(context, idempotencyStore, out var hashGenerator);
        var badRequest = new LinkCreateRequest { OriginalLink = "not-a-url" };

        var first = await sut.CreateLink(badRequest, CancellationToken.None, "bad-key");
        var second = await sut.CreateLink(badRequest, CancellationToken.None, "bad-key");

        var firstResult = Assert.IsType<ObjectResult>(first.Result);
        var secondResult = Assert.IsType<ObjectResult>(second.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, firstResult.StatusCode);
        Assert.Equal(StatusCodes.Status400BadRequest, secondResult.StatusCode);
        hashGenerator.Verify(x => x.Generate(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task CreateLink_RealWorkThrows_EvictsTheClaimInsteadOfPoisoningTheKey()
    {
        await using var context = NewContext();
        var idempotencyStore = new FakeIdempotencyStore();
        var sut = NewController(context, idempotencyStore, out var hashGenerator);
        hashGenerator.Setup(x => x.Generate(It.IsAny<string>())).Throws(new InvalidOperationException("boom"));
        var request = new LinkCreateRequest { OriginalLink = "https://example.com" };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.CreateLink(request, CancellationToken.None, "failing-key"));

        // The claim must not survive a failed attempt - otherwise every retry of a transient
        // failure would be permanently stuck seeing "already in progress" for the rest of the
        // in-flight TTL, for an attempt that never actually completed.
        Assert.False(idempotencyStore.Contains("failing-key"));
    }
}
