## What it is

Decoupling a request from its side effects via a message broker — [RabbitMQ](node:rabbitmq) here.
A producer publishes an event and returns immediately; one or more consumers process it later,
independently, at their own pace.

## What it solves

`POST /Links` and `GET /{hash}` (redirect) both need to *respond* fast, but each also triggers work
that doesn't need to finish before the caller gets an answer — persisting the link, recording a
click. Making the HTTP response wait on that work would tie the caller's latency to the
consumer's, for no benefit the caller can see.

## How it works

1. The producer ([LinkApi](node:link-api), [RedirectApi](node:redirect-api)) publishes an event
   and returns to its caller without waiting for it to be processed.
2. A consumer ([ShortenerService](node:shortener-service), [TrafficService](node:traffic-service))
   receives the event independently and does the actual work (a database write here).
3. **If the broker is briefly unreachable**, the publish falls back to a local SQLite queue
   instead of failing the request — `RabbitMqRetryWorker` (shared code, used by every publisher)
   polls that queue and republishes once the broker recovers. The HTTP response never waits on
   either path.
4. Consumers are **redelivery-safe** — RabbitMQ's at-least-once delivery means the same event can
   arrive twice; both `LinkCreatedConsumer` and `ClickTrackedConsumer` check whether the row
   already exists before writing, so a redelivery is a no-op rather than a duplicate or an error.

The trade-off: the producer's response no longer means "this is durably recorded" — only "this
was accepted, and will be recorded soon, even across a broker outage." Every consumer in this
project is written to be safe under that weaker guarantee (redelivery-safe, as above); a consumer
that *isn't* redelivery-safe would be a correctness bug the pattern itself doesn't prevent.

## How it's implemented here

`RabbitMqClient`/`RabbitMqPublisher`/`RabbitMqConsumer` (`src/backend/Shared/Infrastructure`) are
shared across every producer/consumer pair in this project — the SQLite fallback queue and
`RabbitMqRetryWorker` are shared code too, not duplicated per service. Exchange/queue/binding
topology is declared by the application itself at connection time, not loaded from
`infra/rabbitmq/definitions.json` (that file is an unused placeholder for a possible future
static-provisioning approach).

Two independent hops exist today: `LinkApi` → `ShortenerService` (`LinkCreatedEvent`) and
`RedirectApi` → `ShortenerService`/`TrafficService` (`ClickTrackedEvent`, consumed by both, for
different reasons — see [ShortenerService](node:shortener-service)).

## Pitfalls

- 🐛 [A failed RabbitMQ connection attempt was cached forever](pitfall:rabbitmq-client-connection-never-retried)
  (logic-bug) — fixed
- 🐛 [Every publish opened and declared a brand-new AMQP channel](pitfall:rabbitmq-publisher-unpooled-channel-hot-path)
  (cpu-leak) — fixed
- 🐛 [The SQLite fallback retry loop could crash itself permanently](pitfall:rabbitmq-retry-worker-crash-loop)
  (logic-bug) — fixed

## Relatives

### Nodes

- [RabbitMQ](node:rabbitmq)
- [LinkApi](node:link-api)
- [RedirectApi](node:redirect-api)
- [ShortenerService](node:shortener-service)
- [TrafficService](node:traffic-service)

### Patterns

None yet.
