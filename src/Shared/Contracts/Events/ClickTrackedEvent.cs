namespace Contracts.Events;

// TODO: событие "клик по короткой ссылке" — публикуется RedirectApi в RabbitMQ,
// потребляется TrafficService для записи в Postgres Clicks и Mongo ClicksMeta.
public record ClickTrackedEvent
{
}
