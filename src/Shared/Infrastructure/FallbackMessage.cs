namespace Infrastructure;

public sealed record FallbackMessage(string MessageId, string Topic, string Payload, DateTime CreatedAt);
