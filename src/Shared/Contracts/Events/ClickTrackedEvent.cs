namespace Contracts.Events;

// TODO: "short link clicked" event — published by RedirectApi to RabbitMQ,
// consumed by TrafficService to write to Postgres Clicks and Mongo ClicksMeta.
public record ClickTrackedEvent
{
}
