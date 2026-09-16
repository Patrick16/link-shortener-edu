namespace Contracts.Events;

// TODO: событие "ссылка создана" — публикуется LinkApi в RabbitMQ,
// потребляется ShortenerService для генерации hash и записи в Postgres Links.
public record LinkCreatedEvent
{
}
