using System.Diagnostics;
using System.Threading.Channels;

namespace Infrastructure;

// Decouples "the HTTP request is done" from "the event has been handed to RabbitMQ (or its SQLite
// fallback)". LinkApi's CreateLink and RedirectApi's RedirectToOrigin used to await
// IMessagePublisher.PublishAsync directly, so any RabbitMQ-side slowness - a connection-
// establishment burst, the shared connection reconnecting - showed up as request latency (see
// sandbox/docs/pgcat-pool-sizing.md, Symptom 7: a single click.tracked publish observed at 57.7s
// under exactly this condition). Enqueueing onto this in-memory, bounded channel instead takes
// microseconds regardless of RabbitMQ's state; LocalPublishQueueWorker drains it off the request's
// critical path, calling the same IMessagePublisher.PublishAsync (pooled channels + SQLite fallback,
// unchanged) a controller used to call directly.
//
// Trade-off, by design: this only protects against "RabbitMQ is slow", not "the process is killed
// before this event is sent" - an in-memory queue can't survive a hard process kill. A graceful
// shutdown (SIGTERM, docker-compose stop) drains whatever is queued before the host stops (see
// LocalPublishQueueWorker.StopAsync); only a hard crash between enqueue and drain can still lose an
// item, the same risk every in-memory buffer carries. RabbitMQ actually being unreachable is still
// covered end-to-end by IMessagePublisher's own SQLite fallback + RabbitMqRetryWorker, exactly as
// before this queue existed - this queue only ever changes *when* that call happens, never *how* it
// handles failure.
public interface ILocalPublishQueue
{
    ValueTask EnqueueAsync<TMessage>(TMessage message, string topic, CancellationToken cancellationToken);
}

public sealed class LocalPublishQueue : ILocalPublishQueue
{
    // Generous relative to RabbitMqPublisher's own 16-slot channel pool (see PoolCapacity there) -
    // this only needs to absorb a burst until a worker gets to it, not model RabbitMQ's own
    // backpressure (that still happens inside PublishAsync's channel pool). FullMode is Wait: if this
    // somehow fills up (RabbitMQ stuck for a sustained period AND every worker busy), writers block
    // instead of silently dropping events or growing memory without bound.
    private const int Capacity = 4096;

    private readonly Channel<QueuedPublish> _channel = Channel.CreateBounded<QueuedPublish>(
        new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
        });

    internal ChannelReader<QueuedPublish> Reader => _channel.Reader;

    public ValueTask EnqueueAsync<TMessage>(TMessage message, string topic, CancellationToken cancellationToken)
    {
        // Captured now, not resolved when a worker eventually dequeues this: by then Activity.Current
        // is no longer the HTTP request's (and can't be restored to it either - Activity.Current
        // rejects an Activity that was already Stop()'d, which the request's always has been by then).
        // ActivityContext itself has no such restriction - it's a plain value (trace/span id), valid
        // to carry around and hand to StartActivity as an explicit parent well after the fact. Same
        // idea as RabbitMqConsumer's traceparent header propagation, just in-process instead of AMQP.
        var parentContext = Activity.Current?.Context;

        return _channel.Writer.WriteAsync(
            new QueuedPublish((publisher, ct) => publisher.PublishAsync(message, topic, ct, parentContext)),
            cancellationToken);
    }

    // Called once, from LocalPublishQueueWorker.StopAsync, once the host has started shutting down -
    // lets every worker's ReadAllAsync loop drain whatever is already queued and then end on its own,
    // instead of writers racing an abrupt stop.
    internal void Complete() => _channel.Writer.TryComplete();
}

internal sealed record QueuedPublish(Func<IMessagePublisher, CancellationToken, Task> Publish);
