namespace Contracts.Events;

// "link deleted" event - published by LinkApi (manual delete, owner-verified) to RabbitMQ,
// consumed by ShortenerService which marks the row as deleted. The TTL sweep job inside
// ShortenerService never publishes this - it already runs in the process that owns links_db, so
// there's no cross-service hop to make (see LinkExpiryWorker's own comment).
public sealed record LinkDeletedEvent
{
    public required string Hash { get; init; }
}
