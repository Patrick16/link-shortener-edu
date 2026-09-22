namespace AuthApi;

public record RefreshRotationResult(Guid UserId, string RawToken, DateTime ExpiresAt);

public interface IRefreshTokenService
{
    Task<(string RawToken, DateTime ExpiresAt)> IssueAsync(Guid userId, CancellationToken cancellationToken);

    // Null means the presented token was missing, expired, or already used - the caller should
    // treat that the same as "no session" and fall back to a normal login.
    Task<RefreshRotationResult?> RotateAsync(string rawToken, CancellationToken cancellationToken);

    Task RevokeAsync(string rawToken, CancellationToken cancellationToken);
}
