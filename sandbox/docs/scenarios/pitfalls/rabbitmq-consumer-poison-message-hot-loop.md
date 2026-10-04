# A permanently-failing message looped forever instead of ever giving up

**Category:** cpu-leak **Status:** fixed

`RabbitMqConsumer`'s delivery handler wrapped deserialization and the caller's handler in one
`catch (Exception)` that unconditionally nacked with `requeue: true` — no distinction between a
transient failure (a momentary DB blip, worth retrying) and a permanent one (malformed JSON, a
handler that always throws for a given payload, a schema change breaking
`JsonSerializer.Deserialize<TMessage>`), no attempt counter, no backoff, and no dead-letter
exchange configured on the queue. A message that could never succeed got nacked, immediately
redelivered by RabbitMQ, failed again, and repeated — forever, at full speed. That pins the
consumer's CPU, re-runs the same failing DB round trip on every attempt, floods the logs, and the
message never reaches a terminal state, so the queue never drains for that item.

🐛 **Bug** — every failure nacks with `requeue: true`, no matter how many times it's already failed:

```csharp
consumer.ReceivedAsync += async (_, ea) =>
{
    using var activity = StartConsumerActivity(routingKey, ea);
    try
    {
        var json = Encoding.UTF8.GetString(ea.Body.Span);
        var message = JsonSerializer.Deserialize<TMessage>(json);
        if (message is not null)
        {
            await handler(message, cancellationToken);
        }

        await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, cancellationToken);
    }
    catch (Exception ex)
    {
        activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        _logger.LogError(ex, "Handler failed for message on queue {QueueName}, nacking for requeue", queueName);
        await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: true, cancellationToken);
    }
};
```

✅ **Fix** — track attempts per message (keyed by the publisher-assigned `MessageId`, which stays
stable across redeliveries — unlike `DeliveryTag`, which changes every time), add an
attempt-scaled delay to blunt the hot-loop cost even during the retry window, and give up (nack
without requeue) once `MaxDeliveryAttempts` is hit instead of looping forever:

```csharp
internal async Task HandleDeliveryAsync<TMessage>(
    IChannel channel, string queueName, string routingKey, BasicDeliverEventArgs ea,
    Func<TMessage, CancellationToken, Task> handler, CancellationToken cancellationToken)
{
    using var activity = StartConsumerActivity(routingKey, ea);
    var messageId = ea.BasicProperties.MessageId ?? ea.DeliveryTag.ToString();
    try
    {
        var json = Encoding.UTF8.GetString(ea.Body.Span);
        var message = JsonSerializer.Deserialize<TMessage>(json);
        if (message is not null)
        {
            await handler(message, cancellationToken);
        }

        await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, cancellationToken);
        _deliveryAttempts.TryRemove(messageId, out _);
    }
    catch (Exception ex)
    {
        activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        var attempts = _deliveryAttempts.AddOrUpdate(messageId, 1, (_, count) => count + 1);
        if (attempts >= MaxDeliveryAttempts)
        {
            _deliveryAttempts.TryRemove(messageId, out _);
            _logger.LogError(ex, "Handler failed for message {MessageId} on queue {QueueName} after " +
                "{Attempts} attempts - dropping it instead of requeuing forever", messageId, queueName, attempts);
            await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, cancellationToken);
            return;
        }

        _logger.LogWarning(ex, "Handler failed for message {MessageId} on queue {QueueName} " +
            "(attempt {Attempt}/{MaxAttempts}), nacking for requeue", messageId, queueName, attempts, MaxDeliveryAttempts);

        var delay = TimeSpan.FromMilliseconds(
            Math.Min(InitialHandlerRetryDelay.TotalMilliseconds * attempts, MaxHandlerRetryDelay.TotalMilliseconds));
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

        await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: true, cancellationToken);
    }
}
```

There's still no dead-letter exchange configured on these queues, so past the attempt cap
(5) the message is genuinely dropped rather than parked somewhere inspectable — a deliberate,
comment-documented trade-off over building out a DLQ and something to drain it, not an oversight.

The finding was logged against commit `d88ce9f` (which introduced this handler), but the fix wasn't
applied until much later, bundled into commit `3c79e80` — a large, unrelated grab-bag commit
(frontend test additions, a `Sault`→`Salt` migration, a refresh-token cleanup worker, internal
API-key auth). The snippets above are the isolated hunk, not the full commit diff.

(fixed in `3c79e80`)

## Relatives

### Nodes

- [ShortenerService](node:shortener-service)
- [TrafficService](node:traffic-service)

### Patterns

- [Async messaging](pattern:async-messaging)
