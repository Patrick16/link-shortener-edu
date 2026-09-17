namespace Contracts.Events;

// Routing keys used on the shared events exchange (see Infrastructure.MessagingConstants.EventsExchange).
// Kept next to the event DTOs so publishers and consumers can't drift apart on the string value.
public static class Topics
{
    public const string LinkCreated = "link.created";
    public const string ClickTracked = "click.tracked";
}
