# A "preview" truncation limit reused on a "republish verbatim" path can corrupt the one copy left

**Category:** resilience-gap **Status:** fixed

Watch for this any time one helper method serves two callers with different needs — a
non-destructive "peek/preview" read and a destructive "take this and then delete the source" read
— and both reuse the same size/truncation limit because it's convenient to share the code. A
preview only needs to show a human a sample; truncating it is harmless. A verbatim-republish
operation needs the *exact* bytes, and is especially dangerous to get wrong when the step right
after it is an irreversible delete of the only other copy. If the external API being called
silently truncates past a byte limit while still reporting the *true* original size in a separate
field, and the caller never checks that field, truncation becomes invisible — the caller has no
way to tell "this is the full payload" from "this got cut" just by looking at what came back.

**Concrete instance in this project:** `RabbitMqService.GetMessagesAsync` (the shared helper behind
both the DLQ sample/peek endpoint and the DLQ replay endpoint — see
[Async messaging](pattern:async-messaging)'s dead-letter handling) hardcoded
`truncate: 50_000` for both callers. RabbitMQ's Management API genuinely needs that cap for the
*sample* path (nothing more than a human-readable preview), but replay's own sequence — peek the
message, publish it to the live queue verbatim, then permanently remove it from the dead queue —
reused the exact same call. The Management API's `truncate` parameter clips the returned `payload`
field at that many bytes for *any* message larger than the limit, while still correctly reporting
the real size in a separate `payload_bytes` field — and nothing in `GetMessagesAsync` ever compared
the two. A dead-lettered message over 50,000 bytes would come back truncated, get republished
truncated (now corrupted, invalid JSON in practice), and then have its one full original
permanently deleted from the dead queue by the very next call in the same sequence — replay's
entire reason for existing (recovering a message, not losing it) inverted for exactly the messages
large enough to need the limit raised. Live-confirmed with a real 60,000-byte test payload: `get`
with `truncate: 50000` returned exactly 50,000 bytes of `payload` while `payload_bytes` correctly
said 60000.

⚠️ **Mistake** — `sandbox/infra/control-api/Services/RabbitMqService.cs` (before commit `253bd94`):

```csharp
public async Task<DlqReplayResult?> ReplayDeadLetterQueueAsync(string deadQueueName, int count, CancellationToken ct)
{
    // ...
    var messages = await GetMessagesAsync(deadQueueName, count, requeue: true, ct);
    if (messages.Count == 0)
    {
        return new DlqReplayResult(deadQueueName, 0);
    }

    foreach (var message in messages)
    {
        await PublishToLiveQueueAsync(liveQueueName, message, ct);
    }

    await GetMessagesAsync(deadQueueName, messages.Count, requeue: false, ct);
    return new DlqReplayResult(deadQueueName, messages.Count);
}

private async Task<List<ManagementMessage>> GetMessagesAsync(string queueName, int count, bool requeue, CancellationToken ct)
{
    // ...
    var request = new ManagementGetRequest(count, requeue ? "ack_requeue_true" : "ack_requeue_false", "auto", 50_000);
    // ... same 50,000-byte cap used for both the sample preview AND a verbatim replay
}

private sealed record ManagementMessage(
    string Payload,
    [property: JsonPropertyName("payload_encoding")] string PayloadEncoding,
    ManagementMessageProperties? Properties,
    [property: JsonPropertyName("routing_key")] string RoutingKey);
    // no PayloadBytes field at all - the one signal that would have revealed truncation was never read
```

✅ **Do this instead** (`253bd94`) — give the truncate limit a name tied to its one legitimate use,
pass an effectively unbounded limit on the replay path instead of reusing it, and add a defense-in-
depth check that refuses to proceed if truncation happened anyway:

```csharp
// Only the display preview gets truncated - a human reading a dead message's JSON body never
// needs more than this, and it keeps the panel's payload box small. Replay below deliberately
// does NOT reuse this limit - see its own comment.
private const int SamplePreviewTruncateBytes = 50_000;

// Deliberately does NOT pass SamplePreviewTruncateBytes here (review finding F1): that limit
// is fine for a human-readable preview, but this payload gets republished verbatim - the
// management API silently truncates "payload" past the given byte count while still
// reporting the real size in "payload_bytes", so reusing the preview's 50,000-byte cap would
// let replay silently publish a corrupted, truncated body and then permanently delete the
// one real copy of the original. int.MaxValue as the truncate limit means "don't truncate" in
// practice; VerifyNotTruncated below is a second, explicit guard against the same failure
// mode in case some future caller ever does pass a real limit here.
var messages = await GetMessagesAsync(deadQueueName, count, requeue: true, int.MaxValue, ct);
if (messages.Count == 0)
{
    return new DlqReplayResult(deadQueueName, 0);
}

foreach (var message in messages)
{
    VerifyNotTruncated(deadQueueName, message);
}

foreach (var message in messages)
{
    await PublishToLiveQueueAsync(liveQueueName, message, ct);
}

await GetMessagesAsync(deadQueueName, messages.Count, requeue: false, int.MaxValue, ct);

// Defense-in-depth against the truncation failure mode described in ReplayDeadLetterQueueAsync's
// own comment (review finding F1): compares the management API's reported original size
// (payload_bytes) against the actual size of the payload we got back, decoded per its own
// encoding. A mismatch means the body was truncated server-side despite the large truncate limit
// we pass - republishing it would corrupt the message and the step after this would then delete
// the only full copy, so this throws instead (surfaced to the caller as a 500, not a silent
// corrupt-then-delete).
private static void VerifyNotTruncated(string deadQueueName, ManagementMessage message)
{
    var actualBytes = message.PayloadEncoding == "base64"
        ? Convert.FromBase64String(message.Payload).LongLength
        : System.Text.Encoding.UTF8.GetByteCount(message.Payload);

    if (actualBytes != message.PayloadBytes)
    {
        throw new InvalidOperationException(
            $"Refusing to replay a message from '{deadQueueName}': the management API reported " +
            $"{message.PayloadBytes} original bytes but returned only {actualBytes} - replaying a " +
            "truncated payload would corrupt it and then permanently delete the only full copy.");
    }
}
```

Live-verified afterward with the same 60,000-byte test payload: replayed successfully, and a direct
Management API query confirmed the republished (and re-dead-lettered, since there's no live
consumer for the test queue) copy was still exactly 60,000 bytes, byte-for-byte intact.

**Provenance:** found by `feature-review` on commit `79d520e`
(`.notes/review-reports/2026-10-28-1717-dlq-replay-tooling.md`, F1), fixed in `253bd94`.

## Relatives

### Nodes

- [RabbitMQ](node:rabbitmq)

### Patterns

- [Async messaging](pattern:async-messaging)
