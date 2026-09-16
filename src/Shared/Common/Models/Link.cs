namespace Common.Models;

public record Link(
    string Hash,
    string OriginalLink,
    string ShortenLink,
    Guid? UserId);
