namespace Common.Models;

public record Link(
    string Hash,
    string OriginalLink,
    string ShortenLink,
    DateTime CreatedAt,
    Guid? UserId,
    int ClickCount = 0);
