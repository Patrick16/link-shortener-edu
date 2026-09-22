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
            .FirstOrDefaultAsync(x => x.TokenHash == tokenHash, cancellationToken);

        if (existing is null || existing.ExpiresAt <= DateTime.UtcNow)
        {
            return null;
        }

        if (existing.RevokedAt is not null)
        {
            // This exact token was already rotated away or revoked once before. Seeing it again
            // means either a stolen copy is being replayed, or a client retried a request that
            // already succeeded - either way, don't hand out a new token from it. Burn every other
            // live token for this user too, so a leaked token can't keep refreshing indefinitely.
            await RevokeAllForUserAsync(existing.UserId, cancellationToken);
            return null;
        }

        existing.RevokedAt = DateTime.UtcNow;

        // IssueAsync's SaveChangesAsync flushes both this revocation and the new token insert.
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
