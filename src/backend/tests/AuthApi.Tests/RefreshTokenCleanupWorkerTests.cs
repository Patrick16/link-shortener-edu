using AuthApi.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AuthApi.Tests;

// SQLite in-memory, not EF Core's InMemory provider: CleanupOnceAsync uses ExecuteDeleteAsync (a
// single bulk DELETE), which InMemory doesn't support at all (it only translates LINQ against
// SQL-backed providers) - same reasoning as RefreshTokenServiceTests for RotateAsync's
// ExecuteUpdateAsync.
public class RefreshTokenCleanupWorkerTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public RefreshTokenCleanupWorkerTests() => _connection.Open();

    public void Dispose() => _connection.Dispose();

    private RefreshTokenCleanupWorker NewSut()
    {
        var services = new ServiceCollection();
        services.AddDbContext<DatabaseContext>(options => options.UseSqlite(_connection));
        var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<DatabaseContext>().Database.EnsureCreated();

        return new RefreshTokenCleanupWorker(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<RefreshTokenCleanupWorker>.Instance);
    }

    private static RefreshToken NewToken(DateTime expiresAt, DateTime? revokedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        UserId = Guid.NewGuid(),
        TokenHash = Guid.NewGuid().ToString(),
        CreatedAt = DateTime.UtcNow.AddDays(-30),
        ExpiresAt = expiresAt,
        RevokedAt = revokedAt,
    };

    [Fact]
    public async Task CleanupOnceAsync_DeletesOnlyExpiredTokens()
    {
        var sut = NewSut();
        var provider = new ServiceCollection().AddDbContext<DatabaseContext>(o => o.UseSqlite(_connection)).BuildServiceProvider();
        await using var context = provider.GetRequiredService<DatabaseContext>();
        var expired = NewToken(DateTime.UtcNow.AddDays(-1));
        var expiredAndRevoked = NewToken(DateTime.UtcNow.AddDays(-1), revokedAt: DateTime.UtcNow.AddDays(-2));
        var stillLive = NewToken(DateTime.UtcNow.AddDays(13));
        context.RefreshTokens.AddRange(expired, expiredAndRevoked, stillLive);
        await context.SaveChangesAsync();

        var deleted = await sut.CleanupOnceAsync(CancellationToken.None);

        Assert.Equal(2, deleted);
        var remaining = await context.RefreshTokens.Select(x => x.Id).ToListAsync();
        Assert.Equal([stillLive.Id], remaining);
    }

    [Fact]
    public async Task CleanupOnceAsync_NoExpiredTokens_DeletesNothing()
    {
        var sut = NewSut();
        var provider = new ServiceCollection().AddDbContext<DatabaseContext>(o => o.UseSqlite(_connection)).BuildServiceProvider();
        await using var context = provider.GetRequiredService<DatabaseContext>();
        context.RefreshTokens.Add(NewToken(DateTime.UtcNow.AddDays(1)));
        await context.SaveChangesAsync();

        var deleted = await sut.CleanupOnceAsync(CancellationToken.None);

        Assert.Equal(0, deleted);
    }
}
