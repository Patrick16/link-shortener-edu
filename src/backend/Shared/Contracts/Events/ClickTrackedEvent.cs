namespace Contracts.Events;

// "short link clicked" event — published by RedirectApi to RabbitMQ after it resolves a hash,
// consumed by TrafficService to persist a Click row. Mirrors Common.Models.Click's shape.
public sealed record ClickTrackedEvent
{
    public required Guid Id { get; init; }
    public required string Hash { get; init; }
    public required string InboundLink { get; init; }
    public required string OutboundLink { get; init; }
    public required DateTime ClickedAt { get; init; }
}
