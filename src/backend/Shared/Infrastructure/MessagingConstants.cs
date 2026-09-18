namespace Infrastructure;

// Single source of truth for the RabbitMQ exchange topology, shared between publishers and consumers.
public static class MessagingConstants
{
    public const string EventsExchange = "events";
}
