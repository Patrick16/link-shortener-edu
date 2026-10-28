# A `PropertyNameCaseInsensitive` option only relaxes deserializing, not what gets written on serialize

**Category:** infrastructure-bug **Status:** fixed

Watch for this any time code calls an external HTTP API that itself requires a specific JSON key
casing (lowercase, snake_case, ...) via `System.Text.Json`, and the client sets
`JsonSerializerOptions.PropertyNameCaseInsensitive = true` somewhere nearby. That option sounds
like it should make casing a non-issue in both directions — it doesn't. It only relaxes matching
when *reading* a response into a C# type; it has no effect on what case `System.Text.Json` writes
when *serializing* a request. A request DTO with no explicit `[JsonPropertyName]` attributes gets
serialized in whatever casing its C# property names use (PascalCase, by .NET convention) — if the
API being called only accepts lowercase keys, every request that relies on the option to cover
both directions is silently malformed and gets rejected outright.

**Concrete instance in this project:** `RabbitMqService`'s two new Management HTTP API calls (get
messages from a dead-letter queue, publish a message back onto the live one — see
[Async messaging](pattern:async-messaging)'s dead-letter handling) needed request DTOs
(`ManagementGetRequest`, `ManagementPublishRequest`) for RabbitMQ's `.../get` and
`.../publish` endpoints, which only accept lowercase keys (`count`, `ackmode`, `properties`,
`payload`, ...). `ManagementJsonOptions` already set `PropertyNameCaseInsensitive = true` — reused
from the existing `rabbitmqctl --formatter=json` output parsing elsewhere in the same class, where
it genuinely does cover response deserialization. Carrying that same options object over to the new
*outgoing* request calls gave a false sense that casing was already handled. It wasn't: with no
`[JsonPropertyName]` attributes on the new DTOs, `PostAsJsonAsync` serialized fields like `Count`/
`Ackmode`/`Truncate` in PascalCase, and RabbitMQ's Management API rejected every call outright with
a `key_missing` error — confirmed live against a real dead-lettered message before this was fixed,
so both the sample and replay endpoints 400'd on their very first real use.

This was caught during manual live-testing before the feature's first commit landed, so there's no
separate buggy commit to diff against its fix — reconstructed below from the described failure and
confirmed against the current DTOs in `sandbox/infra/control-api/Services/RabbitMqService.cs` at
HEAD, which already carry the fix.

⚠️ **Mistake** — reconstructed: a request DTO with no explicit property-name casing, relying on
`PropertyNameCaseInsensitive` (set on the same `JsonSerializerOptions` instance used for the call)
to somehow cover both directions:

```csharp
private static readonly JsonSerializerOptions ManagementJsonOptions = new() { PropertyNameCaseInsensitive = true };

private sealed record ManagementGetRequest(int Count, string Ackmode, string Encoding, int Truncate);

// PostAsJsonAsync(..., new ManagementGetRequest(5, "ack_requeue_true", "auto", 50_000), ManagementJsonOptions, ct)
// serializes {"Count":5,"Ackmode":"ack_requeue_true","Encoding":"auto","Truncate":50000} -
// RabbitMQ's Management API rejects every key as unrecognized (key_missing).
```

✅ **Do this instead** — explicit lowercase `[JsonPropertyName]` on every field of a request DTO
bound for an API with a fixed casing contract, regardless of what deserialization options are set
elsewhere on the same client:

```csharp
// sandbox/infra/control-api/Services/RabbitMqService.cs
// Shapes matching the RabbitMQ management API's own JSON contract (snake_case) for the
// "get messages from a queue" and "publish to an exchange" endpoints - see
// https://www.rabbitmq.com/docs/management#http-api for the reference this was built against.
// Every field needs an explicit lowercase JsonPropertyName: PropertyNameCaseInsensitive (set on
// ManagementJsonOptions) only relaxes DESERIALIZATION matching, it doesn't change what case
// System.Text.Json writes on serialize - without these, PostAsJsonAsync would send "Count"/
// "Ackmode"/etc. and RabbitMQ's management API rejects them outright with key_missing errors
// (confirmed live against a real dead-lettered message before this fix).
private sealed record ManagementGetRequest(
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("ackmode")] string Ackmode,
    [property: JsonPropertyName("encoding")] string Encoding,
    [property: JsonPropertyName("truncate")] int Truncate);
```

Anyone adding a future Management API call on this same client needs to remember this explicitly —
`PropertyNameCaseInsensitive` being already set nearby is exactly what makes it easy to assume
casing is a solved problem for requests too.

(found and fixed pre-commit, during manual live-testing ahead of commit `79d520e` — no separate
buggy commit exists to diff; see `.notes/feature-docs/2026-10-28-1729-dlq-replay-tooling.md`,
"Bugs found and fixed along the way" #1. Confirmed against
`sandbox/infra/control-api/Services/RabbitMqService.cs` at HEAD, which already carries the fix.)

## Relatives

### Nodes

- [RabbitMQ](node:rabbitmq)

### Patterns

- [Async messaging](pattern:async-messaging)
