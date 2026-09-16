namespace Contracts.Events;

// TODO: "link created" event — published by LinkApi to RabbitMQ,
// consumed by ShortenerService to generate the hash and write to Postgres Links.
public record LinkCreatedEvent
{
}
