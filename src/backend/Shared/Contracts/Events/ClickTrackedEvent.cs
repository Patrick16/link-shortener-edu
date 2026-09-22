namespace Contracts.Events;

// "short link clicked" event — published by RedirectApi to RabbitMQ after it resolves a hash,
// consumed by TrafficService to persist a Click row (Postgres) and a ClickMeta document (Mongo).
// UserAgent/Referrer/IpAddress are captured raw here, straight off the HTTP request — parsing
// (browser/OS/device) and geo resolution happen downstream in TrafficService, off the redirect's
// hot path.
public sealed record ClickTrackedEvent
{
    public required Guid Id { get; init; }
    public required string Hash { get; init; }
    public required string InboundLink { get; init; }
    public required string OutboundLink { get; init; }
    public required DateTime ClickedAt { get; init; }
    public required string UserAgent { get; init; }
    public required string Referrer { get; init; }
    public string? IpAddress { get; init; }
}
