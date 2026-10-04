# Every publish opened and declared a brand-new AMQP channel

**Category:** cpu-leak **Status:** fixed

`RabbitMqPublisher` opened a new AMQP channel and re-issued `ExchangeDeclareAsync` on *every
single publish* — no pooling or reuse. This was tolerable while only `LinkApi` used it, but once
`RedirectApi` started publishing a `ClickTrackedEvent` on every redirect (the highest-QPS endpoint
in the whole system), each `GET /{hash}` — even a Redis cache hit — paid a full extra broker round
trip (channel open + exchange declare + publish, at least 3 AMQP frames) before returning its 302.

Found by profiling a live k6 run, not by reading the code: `redirect-api` pinned at 90-120% CPU
per replica while Postgres sat at ~33% and PgCat at ~38%, and RabbitMQ's own `channel_created`
counter (`GET /api/nodes` on the management API) tracked 1:1 with the cumulative request count.
Scaling from 1 to 4 replicas only moved throughput 2215 → 2742 RPS — the API layer, not the
database, was the bottleneck.

🐛 **Bug** — open, declare, publish, on every call:

```csharp
public async Task PublishAsync(...)
{
    using var channel = await connection.CreateChannelAsync();
    await channel.ExchangeDeclareAsync(exchange, ExchangeType.Topic);
    await channel.BasicPublishAsync(exchange, routingKey, body);
}
```

✅ **Fix** — a bounded pool (16) of already-open, already-exchange-declared channels, reused
across publishes:

```csharp
public async Task PublishAsync(...)
{
    var channel = await _channelPool.RentAsync(); // exchange already declared once, up front
    try { await channel.BasicPublishAsync(exchange, routingKey, body); }
    finally { _channelPool.Return(channel); }
}
```

**Effect, measured on the same 4-replica/300-VU test: 2742 → 6052 RPS, avg latency 83ms → 37ms,
p95 205ms → 23ms.**

(fixed in `57945c6`)

## Relatives

### Nodes

- [RedirectApi](node:redirect-api) — where this was found (the hottest path)
- [LinkApi](node:link-api) — used the same unpooled publisher
- [RabbitMQ](node:rabbitmq)

### Patterns

- [Async messaging](pattern:async-messaging)
