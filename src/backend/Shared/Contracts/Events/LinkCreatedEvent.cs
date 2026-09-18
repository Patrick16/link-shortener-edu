namespace Contracts.Events;

// "link created" event — published by LinkApi (hash already generated) to RabbitMQ,
// consumed by ShortenerService which persists it as-is to Postgres Links.
public sealed record LinkCreatedEvent
{
    public required string Hash { get; init; }
    public required string OriginalLink { get; init; }
    public required string ShortenLink { get; init; }
    public required DateTime CreatedAt { get; init; }
    public Guid? UserId { get; init; }
}
