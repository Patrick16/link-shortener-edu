using System.Diagnostics;

namespace Infrastructure;

// Manual distributed-tracing spans for RabbitMQ publish/consume — there's no mainstream
// auto-instrumentation package for RabbitMQ.Client, so producer/consumer spans (and the
// "traceparent" header that links them across the async boundary) are hand-rolled here.
// Registered with the OTel TracerProvider in Shared/ServiceDefaults/Extensions.cs.
public static class MessagingActivitySource
{
    public const string Name = "LinkShortener.Messaging";
    public static readonly ActivitySource Instance = new(Name);
}
