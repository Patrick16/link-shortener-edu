using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Infrastructure;

public sealed class RabbitMqConsumer(
    IRabbitMqConnection connection,
    ILogger<RabbitMqConsumer> logger,
    IConfiguration configuration) : IMessageConsumer
{
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    // A message that keeps failing across repeated batches (a handler bug, or the database being
    // down long enough to exhaust this consumer's patience) must not be nacked-with-requeue forever -
    // RabbitMQ redelivers immediately, which would hot-loop the same failing round trip at full speed
    // with the queue never draining. After this many attempts the message is dead-lettered to
    // "{queueName}.dead" instead of being retried again - see HandleBatchFailureAsync.
    private const int MaxDeliveryAttempts = 5;
    private static readonly TimeSpan InitialHandlerRetryDelay = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan MaxHandlerRetryDelay = TimeSpan.FromSeconds(2);

    private readonly IRabbitMqConnection _connection = connection;
    private readonly ILogger<RabbitMqConsumer> _logger = logger;

    // Keyed by the publisher-assigned MessageId (stable across redeliveries of the same logical
    // message, unlike DeliveryTag which changes every redelivery). Entries are removed as soon as a
    // message is acked or dead-lettered, so this only ever holds messages currently mid-retry - it
    // resets on process restart, which just means a message gets a fresh set of attempts after a
    // restart rather than picking up its old count; acceptable here since the point is bounding a
    // single consumer's hot loop, not a durable retry ledger.
    private readonly ConcurrentDictionary<string, int> _deliveryAttempts = new();

    // How many unacked deliveries this consumer's channel can hold at once - the classic
    // competing-consumers throughput/fairness knob. Config-bound (not hardcoded) specifically so
    // control-api's experimental prefetch control can change it via a container recreate - see
    // RabbitMq__PrefetchCount in docker-compose.yml.
    private readonly ushort _prefetchCount = configuration.GetValue<ushort?>("RabbitMq:PrefetchCount") ?? 10;

    // How many deliveries accumulate before a batch is flushed to the handler, and how long a
    // partial batch is allowed to sit before flushing anyway. The two knobs trade DB-round-trip
    // savings (bigger/slower) against per-message latency (smaller/faster).
    private readonly int _batchSize = configuration.GetValue<int?>("RabbitMq:BatchSize") ?? 100;
    private readonly TimeSpan _batchTimeout =
        TimeSpan.FromMilliseconds(configuration.GetValue<int?>("RabbitMq:BatchTimeoutMs") ?? 500);

    public async Task ConsumeBatchAsync<TMessage>(
        string queueName,
        string routingKey,
        Func<IReadOnlyList<BatchItem<TMessage>>, CancellationToken, Task<BatchOutcome>> handler,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(queueName);
        ArgumentException.ThrowIfNullOrEmpty(routingKey);
        ArgumentNullException.ThrowIfNull(handler);

        // Runs (and keeps this call alive) until the caller cancels. Each pass is one channel's
        // lifetime: if the broker restarts or the connection drops, the channel shuts down, the pass
        // ends, and the next one re-declares the topology and re-subscribes. Without this loop a
        // broker restart left the consumer subscribed to nothing while /health/ready (which reconnects
        // on its own) stayed green - observed live: 0 consumers on every queue after a broker restart.
        var retryDelay = InitialRetryDelay;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // The broker being briefly unreachable at startup (a container health check that
                // passed before the AMQP listener actually opened, a restart, ...) shouldn't crash
                // this worker - that's exactly the kind of transient condition retrying should absorb.
                var channel = await SetUpChannelWithRetryAsync(queueName, routingKey, cancellationToken).ConfigureAwait(false);
                await using (channel.ConfigureAwait(false))
                {
                    await ConsumeUntilChannelClosesAsync(channel, queueName, handler, cancellationToken).ConfigureAwait(false);
                }

                retryDelay = InitialRetryDelay;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // e.g. an ack/nack on a channel that died mid-batch - same recovery as a clean
                // shutdown; the unacked deliveries are redelivered to the next subscription.
                _logger.LogWarning(ex, "RabbitMQ consumer for queue {QueueName} failed, re-subscribing in {Delay}", queueName, retryDelay);
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, MaxRetryDelay.TotalSeconds));
                continue;
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("RabbitMQ channel for queue {QueueName} closed - re-subscribing", queueName);
                await Task.Delay(InitialRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ConsumeUntilChannelClosesAsync<TMessage>(
        IChannel channel,
        string queueName,
        Func<IReadOnlyList<BatchItem<TMessage>>, CancellationToken, Task<BatchOutcome>> handler,
        CancellationToken cancellationToken)
    {
        var deadQueueName = DeadQueueName(queueName);

        // Cancelled when the caller stops us *or* this channel shuts down, so the flush loop (which
        // otherwise blocks forever waiting for deliveries a dead channel will never send) unwinds and
        // the caller can open a fresh one.
        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        channel.ChannelShutdownAsync += (_, _) =>
        {
            sessionCts.Cancel();
            return Task.CompletedTask;
        };
        if (!channel.IsOpen)
        {
            return;
        }

        var sessionToken = sessionCts.Token;

        // Bounded specifically so it applies real backpressure: once _batchSize deliveries are
        // sitting here waiting for the next flush, ReceiveDeliveryAsync's WriteAsync below blocks,
        // which in turn stalls RabbitMQ's own push once the prefetch window is also exhausted.
        var buffer = Channel.CreateBounded<BufferedDelivery<TMessage>>(
            new BoundedChannelOptions(_batchSize) { FullMode = BoundedChannelFullMode.Wait });

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, ea) =>
            ReceiveDeliveryAsync(channel, deadQueueName, buffer.Writer, ea, sessionToken);

        await channel.BasicConsumeAsync(queueName, autoAck: false, consumer, sessionToken);

        await RunFlushLoopAsync(channel, queueName, deadQueueName, buffer.Reader, handler, sessionToken)
            .ConfigureAwait(false);
    }

    internal async Task ReceiveDeliveryAsync<TMessage>(
        IChannel channel,
        string deadQueueName,
        ChannelWriter<BufferedDelivery<TMessage>> writer,
        BasicDeliverEventArgs ea,
        CancellationToken cancellationToken)
    {
        using var activity = StartConsumerActivity(ea);
        var messageId = ea.BasicProperties.MessageId ?? ea.DeliveryTag.ToString();

        // Snapshotted before any await - RabbitMQ.Client may reuse ea.Body's underlying buffer once
        // this handler yields, so anything read from it has to happen synchronously up front.
        var rawBody = ea.Body.ToArray();

        TMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<TMessage>(Encoding.UTF8.GetString(rawBody));
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            message = default;
        }

        if (message is null)
        {
            _logger.LogWarning(
                "Message {MessageId} could not be deserialized to {MessageType} - dead-lettering it to {DeadQueueName}",
                messageId,
                typeof(TMessage).Name,
                deadQueueName);
            await PublishDeadLetterAsync(channel, deadQueueName, rawBody, messageId, "deserialize-failed", cancellationToken)
                .ConfigureAwait(false);
            await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Debug, not Information - one per message, same volume as the publish side. The batch this
        // message ends up in is what gets an Information-level summary, in FlushBatchAsync below.
        _logger.LogDebug("Received message {MessageId} ({RoutingKey})", messageId, ea.RoutingKey);

        await writer.WriteAsync(new BufferedDelivery<TMessage>(ea.DeliveryTag, messageId, message), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RunFlushLoopAsync<TMessage>(
        IChannel channel,
        string queueName,
        string deadQueueName,
        ChannelReader<BufferedDelivery<TMessage>> reader,
        Func<IReadOnlyList<BatchItem<TMessage>>, CancellationToken, Task<BatchOutcome>> handler,
        CancellationToken cancellationToken)
    {
        var batch = new List<BufferedDelivery<TMessage>>(_batchSize);
        try
        {
            while (true)
            {
                if (!await FillBatchAsync(reader, batch, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                await FlushBatchAsync(channel, queueName, deadQueueName, batch, handler, cancellationToken)
                    .ConfigureAwait(false);
                batch.Clear();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown. Anything still sitting in `batch` here was never acked, so RabbitMQ
            // will simply redeliver it once a consumer picks the queue back up again.
        }
    }

    // Blocks for the first delivery (no point starting a timeout window over an empty batch), then
    // keeps draining the buffer until _batchSize is reached or _batchTimeout elapses since that first
    // item arrived - whichever comes first. Returns false only once the buffer itself is completed
    // (doesn't happen in normal operation; ConsumeBatchAsync never completes the writer).
    internal async Task<bool> FillBatchAsync<TMessage>(
        ChannelReader<BufferedDelivery<TMessage>> reader,
        List<BufferedDelivery<TMessage>> batch,
        CancellationToken cancellationToken)
    {
        if (!await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        using var timeoutCts = new CancellationTokenSource(_batchTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        while (batch.Count < _batchSize)
        {
            if (reader.TryRead(out var item))
            {
                batch.Add(item);
                continue;
            }

            try
            {
                if (!await reader.WaitToReadAsync(linked.Token).ConfigureAwait(false))
                {
                    break;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The per-batch timeout (not the caller's cancellation) elapsed with at least one
                // item already buffered - flush the partial batch instead of waiting for more.
                break;
            }
        }

        return true;
    }

    internal async Task FlushBatchAsync<TMessage>(
        IChannel channel,
        string queueName,
        string deadQueueName,
        List<BufferedDelivery<TMessage>> batch,
        Func<IReadOnlyList<BatchItem<TMessage>>, CancellationToken, Task<BatchOutcome>> handler,
        CancellationToken cancellationToken)
    {
        var items = batch.Select(x => new BatchItem<TMessage>(x.MessageId, x.Message)).ToList();
        var maxDeliveryTag = batch[^1].DeliveryTag;

        // Debug - shows how many individual "Received" log lines just got collapsed into one handler
        // call, which is the whole point of batching (see RabbitMq:BatchSize/BatchTimeoutMs).
        _logger.LogDebug("Flushing batch of {Count} message(s) from queue {QueueName}", batch.Count, queueName);

        BatchOutcome outcome;
        try
        {
            outcome = await handler(items, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex, "Batch handler failed for {Count} message(s) on queue {QueueName}", batch.Count, queueName);
            await HandleBatchFailureAsync(channel, deadQueueName, batch, cancellationToken).ConfigureAwait(false);
            return;
        }

        var poisonIds = new HashSet<string>(outcome.PoisonMessageIds);
        foreach (var delivery in batch)
        {
            _deliveryAttempts.TryRemove(delivery.MessageId, out _);
            if (poisonIds.Contains(delivery.MessageId))
            {
                await PublishDeadLetterAsync(
                        channel,
                        deadQueueName,
                        JsonSerializer.SerializeToUtf8Bytes(delivery.Message),
                        delivery.MessageId,
                        "business-rejected",
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        // One ack for the whole batch: everything in it is now accounted for, either persisted by the
        // handler or safely relocated to the dead queue above.
        await channel.BasicAckAsync(maxDeliveryTag, multiple: true, cancellationToken).ConfigureAwait(false);
    }

    // The handler threw - a batch-wide, presumably transient failure (e.g. the database is down),
    // not something specific to any one message in it. Every message in the batch gets its own
    // attempt count bumped (same ledger single-message retries used to use) and is either requeued
    // for another try or, once it has been retried too many times across however many batches it's
    // landed in, dead-lettered instead of retried forever.
    private async Task HandleBatchFailureAsync<TMessage>(
        IChannel channel,
        string deadQueueName,
        List<BufferedDelivery<TMessage>> batch,
        CancellationToken cancellationToken)
    {
        var giveUp = new List<BufferedDelivery<TMessage>>();
        var retry = new List<BufferedDelivery<TMessage>>();

        foreach (var delivery in batch)
        {
            var attempts = _deliveryAttempts.AddOrUpdate(delivery.MessageId, 1, (_, count) => count + 1);
            (attempts >= MaxDeliveryAttempts ? giveUp : retry).Add(delivery);
        }

        foreach (var delivery in giveUp)
        {
            _logger.LogError(
                "Message {MessageId} failed after {Attempts} attempts as part of a batch - dead-lettering it instead of retrying forever",
                delivery.MessageId,
                MaxDeliveryAttempts);
            _deliveryAttempts.TryRemove(delivery.MessageId, out _);
            await PublishDeadLetterAsync(
                    channel,
                    deadQueueName,
                    JsonSerializer.SerializeToUtf8Bytes(delivery.Message),
                    delivery.MessageId,
                    "retries-exhausted",
                    cancellationToken)
                .ConfigureAwait(false);
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken).ConfigureAwait(false);
        }

        if (retry.Count > 0)
        {
            // A brief, attempt-scaled delay before requeueing so a batch that keeps failing spins at a
            // few retries per second instead of pinning the CPU with immediate redelivery - sized off
            // the worst (highest) attempt count in this group so a message on its 4th try doesn't get
            // the 1st try's short delay.
            var worstAttempts = retry.Max(d => _deliveryAttempts.GetValueOrDefault(d.MessageId, 1));
            var delay = TimeSpan.FromMilliseconds(
                Math.Min(InitialHandlerRetryDelay.TotalMilliseconds * worstAttempts, MaxHandlerRetryDelay.TotalMilliseconds));
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

            foreach (var delivery in retry)
            {
                await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static async Task PublishDeadLetterAsync(
        IChannel channel,
        string deadQueueName,
        ReadOnlyMemory<byte> body,
        string? messageId,
        string reason,
        CancellationToken cancellationToken)
    {
        var properties = new BasicProperties
        {
            MessageId = messageId,
            Headers = new Dictionary<string, object?> { ["x-dead-reason"] = reason },
        };

        // No exchange/binding needed - the dead queue is published to directly by name via the
        // default (nameless) exchange, which routes to a queue whose name matches the routing key.
        await channel.BasicPublishAsync(
                exchange: "",
                routingKey: deadQueueName,
                mandatory: false,
                basicProperties: properties,
                body: body,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    private static string DeadQueueName(string queueName) => $"{queueName}.dead";

    internal async Task<IChannel> SetUpChannelWithRetryAsync(
        string queueName,
        string routingKey,
        CancellationToken cancellationToken)
    {
        var deadQueueName = DeadQueueName(queueName);
        var delay = InitialRetryDelay;
        while (true)
        {
            IChannel? channel = null;
            try
            {
                channel = await _connection.CreateChannelAsync(cancellationToken).ConfigureAwait(false);

                await channel.ExchangeDeclareAsync(
                    MessagingConstants.EventsExchange,
                    ExchangeType.Topic,
                    durable: true,
                    cancellationToken: cancellationToken);

                await channel.QueueDeclareAsync(
                    queueName,
                    durable: true,
                    exclusive: false,
                    autoDelete: false,
                    cancellationToken: cancellationToken);

                await channel.QueueBindAsync(
                    queueName,
                    MessagingConstants.EventsExchange,
                    routingKey,
                    cancellationToken: cancellationToken);

                // No exchange/binding needed for this one - dead letters are published straight to it
                // by name via the default exchange (see PublishDeadLetterAsync), so declaring it is
                // enough.
                await channel.QueueDeclareAsync(
                    deadQueueName,
                    durable: true,
                    exclusive: false,
                    autoDelete: false,
                    cancellationToken: cancellationToken);

                await channel.BasicQosAsync(0, prefetchCount: _prefetchCount, global: false, cancellationToken: cancellationToken);

                return channel;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // The channel may have opened fine even though a later declare/bind/QoS call
                // failed - without disposing it here, every retry round (up to every 30s during a
                // sustained outage) leaks another open channel on the broker.
                if (channel is not null)
                {
                    await channel.DisposeAsync().ConfigureAwait(false);
                }

                _logger.LogWarning(
                    ex,
                    "Failed to set up RabbitMQ consumer for queue {QueueName}, retrying in {Delay}",
                    queueName,
                    delay);

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, MaxRetryDelay.TotalSeconds));
            }
        }
    }

    private static Activity? StartConsumerActivity(BasicDeliverEventArgs ea)
    {
        var parentContext = TryExtractParentContext(ea.BasicProperties);
        var activity = parentContext is { } context
            ? MessagingActivitySource.Instance.StartActivity($"{ea.RoutingKey} consume", ActivityKind.Consumer, context)
            : MessagingActivitySource.Instance.StartActivity($"{ea.RoutingKey} consume", ActivityKind.Consumer);

        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.destination.name", MessagingConstants.EventsExchange);
        activity?.SetTag("messaging.rabbitmq.routing_key", ea.RoutingKey);
        activity?.SetTag("messaging.message.id", ea.BasicProperties.MessageId);
        return activity;
    }

    // The publisher sends the trace context as a "traceparent" header (W3C format) so this span
    // links back to the one that published the message, instead of starting an unrelated trace.
    private static ActivityContext? TryExtractParentContext(IReadOnlyBasicProperties properties)
    {
        if (properties.Headers?.TryGetValue("traceparent", out var raw) == true &&
            raw is byte[] bytes &&
            ActivityContext.TryParse(Encoding.UTF8.GetString(bytes), null, out var context))
        {
            return context;
        }

        return null;
    }
}

// A deserialized delivery sitting in RabbitMqConsumer's internal buffer, waiting for its batch to
// flush. Keeps the DeliveryTag (needed for ack/nack/dead-letter) alongside the payload that
// BatchItem<TMessage> exposes to handlers - handlers never see delivery tags at all.
internal sealed record BufferedDelivery<TMessage>(ulong DeliveryTag, string MessageId, TMessage Message);
