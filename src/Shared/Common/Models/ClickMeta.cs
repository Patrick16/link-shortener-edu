namespace Common.Models;

public record ClickMeta(
    Guid Id,
    DateTime ClickedAt,
    string UserAgent,
    string Referrer,
    string Origin,
    string Headers);
