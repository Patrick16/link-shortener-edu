namespace AuthApi.Models;

// A plain mutable class rather than a record like User: RevokedAt is state that genuinely changes
// after creation (rotation, logout, reuse detection), which doesn't fit init-only properties.
public class RefreshToken
{
    public Guid Id { get; init; }

    public Guid UserId { get; init; }

    // SHA-256 hex digest of the raw token sent to the client - the raw value itself is never
    // stored, same reasoning as password hashing: a DB read alone shouldn't hand out a usable token.
    public string TokenHash { get; init; } = string.Empty;

    public DateTime CreatedAt { get; init; }

    public DateTime ExpiresAt { get; init; }

    public DateTime? RevokedAt { get; set; }
}
