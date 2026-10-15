using Common;
using Contracts.Events;
using Infrastructure;
using Infrastructure.Grpc;

namespace TrafficService;

// Server side of the messaging-mode toggle - see ShortenerService's MessagingGrpcService for the
// multi-topic case; this one only ever needs ClickTrackedEvent, so its topic->handler map has a
// single entry.
public sealed class MessagingGrpcService(ClickTrackedConsumer consumer)
    : MessagingGrpcServiceBase(new Dictionary<string, TopicHandler>
    {
        [Topics.ClickTracked] = ForTopic<ClickTrackedEvent>((item, ct) => consumer.HandleBatchAsync([item], ct)),
    })
{
}
