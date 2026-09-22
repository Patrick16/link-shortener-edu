using AuthApi;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace AuthApi.Tests;

public class RefreshTokenServiceTests
{
    private static DatabaseContext NewContext()
    {
        var options = new DbContextOptionsBuilder<DatabaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new DatabaseContext(options);
    }

    private static IConfiguration Config(int? refreshExpiryDays = null)
    {
        var data = new Dictionary<string, string?>();
        if (refreshExpiryDays is not null)
        {
            data["Jwt:RefreshExpiryDays"] = refreshExpiryDays.Value.ToString();
        }

        return new ConfigurationBuilder().AddInMemoryCollection(data).Build();
    }

    [Fact]
    public async Task IssueAsync_StoresHashNotRawToken()
    {
        await using var context = NewContext();
        var sut = new RefreshTokenService(context, Config());
        var userId = Guid.NewGuid();

        var (rawToken, expiresAt) = await sut.IssueAsync(userId, CancellationToken.None);

        Assert.NotEmpty(rawToken);
        Assert.InRange(expiresAt, DateTime.UtcNow.AddDays(13), DateTime.UtcNow.AddDays(15));

        var stored = await context.RefreshTokens.SingleAsync();
        Assert.Equal(userId, stored.UserId);
        Assert.NotEqual(rawToken, stored.TokenHash);
        Assert.Null(stored.RevokedAt);
    }

    [Fact]
    public async Task IssueAsync_CustomExpiry_IsRespected()
    {
        await using var context = NewContext();
        var sut = new RefreshTokenService(context, Config(refreshExpiryDays: 3));

        var (_, expiresAt) = await sut.IssueAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.InRange(expiresAt, DateTime.UtcNow.AddDays(2), DateTime.UtcNow.AddDays(4));
    }

    [Fact]
    public async Task RotateAsync_ValidToken_RevokesOldAndIssuesNewForSameUser()
    {
        await using var context = NewContext();
        var sut = new RefreshTokenService(context, Config());
        var userId = Guid.NewGuid();
        var (rawToken, _) = await sut.IssueAsync(userId, CancellationToken.None);

        var result = await sut.RotateAsync(rawToken, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(userId, result!.UserId);
        Assert.NotEqual(rawToken, result.RawToken);

        var tokens = await context.RefreshTokens.OrderBy(x => x.CreatedAt).ToListAsync();
        Assert.Equal(2, tokens.Count);
        Assert.NotNull(tokens[0].RevokedAt);
        Assert.Null(tokens[1].RevokedAt);
    }

    [Fact]
    public async Task RotateAsync_UnknownToken_ReturnsNull()
    {
        await using var context = NewContext();
        var sut = new RefreshTokenService(context, Config());

        var result = await sut.RotateAsync("never-issued", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task RotateAsync_ExpiredToken_ReturnsNull()
    {
        await using var context = NewContext();
        context.RefreshTokens.Add(new Models.RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = Guid.NewGuid(),
            TokenHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes("expired-token"))),
            CreatedAt = DateTime.UtcNow.AddDays(-30),
            ExpiresAt = DateTime.UtcNow.AddDays(-1),
        });
        await context.SaveChangesAsync();
        var sut = new RefreshTokenService(context, Config());

        var result = await sut.RotateAsync("expired-token", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task RotateAsync_ReusedToken_RevokesAllTokensForThatUser()
    {
        await using var context = NewContext();
        var sut = new RefreshTokenService(context, Config());
        var userId = Guid.NewGuid();
        var (firstToken, _) = await sut.IssueAsync(userId, CancellationToken.None);
        var firstRotation = await sut.RotateAsync(firstToken, CancellationToken.None);
        Assert.NotNull(firstRotation);

        // Presenting the already-rotated token again looks like a leaked/replayed token.
        var replayResult = await sut.RotateAsync(firstToken, CancellationToken.None);

        Assert.Null(replayResult);
        var liveTokens = await context.RefreshTokens
            .Where(x => x.UserId == userId && x.RevokedAt == null)
            .ToListAsync();
        Assert.Empty(liveTokens);
    }

    [Fact]
    public async Task RevokeAsync_ValidToken_MarksItRevoked()
    {
        await using var context = NewContext();
        var sut = new RefreshTokenService(context, Config());
        var (rawToken, _) = await sut.IssueAsync(Guid.NewGuid(), CancellationToken.None);

        await sut.RevokeAsync(rawToken, CancellationToken.None);

        var stored = await context.RefreshTokens.SingleAsync();
        Assert.NotNull(stored.RevokedAt);
    }

    [Fact]
    public async Task RevokeAsync_UnknownToken_DoesNothing()
    {
        await using var context = NewContext();
        var sut = new RefreshTokenService(context, Config());

        await sut.RevokeAsync("never-issued", CancellationToken.None);

        Assert.Empty(await context.RefreshTokens.ToListAsync());
    }

    [Fact]
    public async Task RevokeAsync_AlreadyRotatedToken_IsHarmlessNoOp()
    {
        await using var context = NewContext();
        var sut = new RefreshTokenService(context, Config());
        var (rawToken, _) = await sut.IssueAsync(Guid.NewGuid(), CancellationToken.None);
        await sut.RotateAsync(rawToken, CancellationToken.None);

        // The rotated-away token is already revoked; RevokeAsync's WHERE clause on RevokedAt ==
        // null should just find nothing and return, not throw.
        var exception = await Record.ExceptionAsync(() => sut.RevokeAsync(rawToken, CancellationToken.None));

        Assert.Null(exception);
    }
}
