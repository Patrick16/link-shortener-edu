namespace Common.Models;

public record Click(
    Guid Id,
    DateTime ClickedAt,
    string InboundLink,
    string OutboundLink,
    string Hash);