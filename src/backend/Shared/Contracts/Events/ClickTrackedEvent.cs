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

    // The link's owner at click time, if any - RedirectController already has `link.UserId` in
    // hand when it builds this event (the same value Live Top Links keys its per-user buckets off),
    // so this is a free addition, not an extra lookup. Additive: existing consumers that don't care
    // about ownership (TrafficService's Postgres/Mongo writes) keep working unchanged. Feeds
    // ReportingService's owner-scoped reporting - see ClickFact.UserId.
    public Guid? UserId { get; init; }
}
