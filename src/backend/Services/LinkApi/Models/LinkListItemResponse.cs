namespace LinkApi.Models;

public record LinkListItemResponse(string ShortenLink, string OriginalLink, DateTime CreatedAt, int ClickCount);
