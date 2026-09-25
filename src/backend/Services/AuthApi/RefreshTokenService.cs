using System.Security.Cryptography;
using System.Text;
using AuthApi.Models;
using Microsoft.EntityFrameworkCore;

namespace AuthApi;

public class RefreshTokenService(DatabaseContext context, IConfiguration configuration) : IRefreshTokenService
{
    private readonly DatabaseContext _context = context;
    private readonly IConfiguration _configuration = configuration;

    public async Task<(string RawToken, DateTime ExpiresAt)> IssueAsync(Guid userId, CancellationToken cancellationToken)
    {
        var rawToken = GenerateRawToken();
        var expiryDays = _configuration.GetValue("Jwt:RefreshExpiryDays", 14);
        var expiresAt = DateTime.UtcNow.AddDays(expiryDays);

        _context.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenHash = Hash(rawToken),
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = expiresAt,
        });
        await _context.SaveChangesAsync(cancellationToken);

        return (rawToken, expiresAt);
    }

    public async Task<RefreshRotationResult?> RotateAsync(string rawToken, CancellationToken cancellationToken)
    {
        var tokenHash = Hash(rawToken);
        var existing = await _context.RefreshTokens
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TokenHash == tokenHash, cancellationToken);

        if (existing is null || existing.ExpiresAt <= DateTime.UtcNow)
        {
            return null;
        }

        // Claim this token atomically: only the caller whose UPDATE actually flips RevokedAt from
        // null to non-null wins the race. Two concurrent calls with the same raw token (a React
        // StrictMode double-invoke, two tabs refreshing at the same instant, or a genuine stolen-
        // token replay racing the real client) used to both read RevokedAt == null before either
        // saved, so both could "win" and the token family would silently fork instead of reuse
        // detection ever firing. ExecuteUpdateAsync's row count tells us definitively which case
        // this is - a plain SELECT-then-write can't.
        var claimed = await _context.RefreshTokens
            .Where(x => x.TokenHash == tokenHash && x.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.RevokedAt, DateTime.UtcNow), cancellationToken);

        if (claimed == 0)
        {
            // This exact token was already rotated away or revoked (by the request that won the
            // race above, or a previous call). Seeing it presented again means either a stolen copy
            // is being replayed, or a client retried a request that already succeeded - either way,
            // don't hand out a new token from it. Burn every other live token for this user too, so
            // a leaked token can't keep refreshing indefinitely.
            await RevokeAllForUserAsync(existing.UserId, cancellationToken);
            return null;
        }

        var (newRawToken, newExpiresAt) = await IssueAsync(existing.UserId, cancellationToken);

        return new RefreshRotationResult(existing.UserId, newRawToken, newExpiresAt);
    }

    public async Task RevokeAsync(string rawToken, CancellationToken cancellationToken)
    {
        var tokenHash = Hash(rawToken);
        var existing = await _context.RefreshTokens
            .FirstOrDefaultAsync(x => x.TokenHash == tokenHash && x.RevokedAt == null, cancellationToken);

        if (existing is null)
        {
            return;
        }

        existing.RevokedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task RevokeAllForUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var liveTokens = await _context.RefreshTokens
            .Where(x => x.UserId == userId && x.RevokedAt == null)
            .ToListAsync(cancellationToken);

        foreach (var liveToken in liveTokens)
        {
            liveToken.RevokedAt = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    private static string GenerateRawToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static string Hash(string rawToken) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));
}
