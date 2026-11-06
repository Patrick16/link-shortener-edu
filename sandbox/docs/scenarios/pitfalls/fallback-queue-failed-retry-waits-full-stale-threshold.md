# A claim-based queue needs an explicit release path for "tried and failed," not just "crashed" and "succeeded"

**Category:** resilience-gap **Status:** fixed

Watch for this any time a retry mechanism uses a claim/lease to stop two workers picking up the same
row, with the claim designed to clear only two ways: the happy path deletes the row, and a
stale-claim timeout recovers a worker that died mid-attempt. It's easy to assume that covers every
case — but "the attempt ran to completion and failed" is a third case. If nothing explicitly
releases the claim on that path, a merely-failed attempt silently inherits the crash-recovery
timeout as its own retry backoff, usually far longer than intended.

**Concrete instance in this project:** the SQLite fallback queue's row claiming (`GetPendingAsync`'s
atomic `UPDATE ... RETURNING`, built specifically to stop two `link-api`/`redirect-api` replicas
sharing the fallback file from double-publishing the same row — see the companion pitfall,
[scaling link-api/redirect-api made the SQLite fallback queue republish messages twice](pitfall:rabbitmq-fallback-store-shared-file-race))
only cleared a claim two ways: `DeleteAsync` on success, or `GetPendingAsync`'s own
`WHERE ClaimedAt IS NULL OR ClaimedAt <= $staleThreshold` treating a 2-minute-old claim as claimable
again — a crash-recovery fallback for a replica that dies mid-tick with no chance to clean up. An
ordinary failed republish attempt (broker still down, no crash at all — the actual common case for
this queue) fell through neither path: the row stayed claimed until that same 2-minute threshold
aged it out, even though `RabbitMqRetryWorker`'s own poll interval is only ~15s average (full
jitter over 30s) — a transient failure backed off roughly 8x longer than the worker's own polling
cadence implies, every time.

⚠️ **Mistake** — `src/backend/Shared/Infrastructure/RabbitMqRetryWorker.cs` (before `5c8475b`): the
failure branch only logs, it never tells the store this attempt is over:

```csharp
else
{
    _logger.LogWarning(
        "Retry failed for fallback message {MessageId} (topic {Topic})",
        message.MessageId,
        message.Topic);
    // Nothing releases the claim GetPendingAsync placed above - the row stays ClaimedAt-stamped
    // until StaleClaimThreshold (2 minutes) ages it out on its own, even though nothing crashed.
}
```

✅ **Do this instead** (`5c8475b`) — give the store an explicit "this attempt is over, try again
sooner" signal, distinct from both the delete-on-success and the age-out-on-crash paths:

```csharp
// IMessageFallbackStore.cs
// Clears ClaimedAt on a row whose republish attempt just failed, so it's eligible again on the
// next poll tick instead of sitting claimed until StaleClaimThreshold elapses.
Task ReleaseClaimAsync(string messageId, CancellationToken cancellationToken);

// SqliteMessageFallbackStore.cs
public async Task ReleaseClaimAsync(string messageId, CancellationToken cancellationToken)
{
    await using var connection = new SqliteConnection(_connectionString);
    await connection.OpenAsync(cancellationToken);

    // GetPendingAsync's own WHERE clause already treats NULL the same as a stale claim, so this
    // is just an earlier, explicit version of the same "eligible again" state.
    await using var command = connection.CreateCommand();
    command.CommandText = $"UPDATE {TableName} SET ClaimedAt = NULL WHERE MessageId = $messageId;";
    command.Parameters.AddWithValue("$messageId", messageId);
    await command.ExecuteNonQueryAsync(cancellationToken);
}

// RabbitMqRetryWorker.cs
else
{
    await _fallbackStore.ReleaseClaimAsync(message.MessageId, cancellationToken);
    _logger.LogWarning(/* ... */);
}
```

`StaleClaimThreshold` itself is unchanged and still the only recovery path for a replica that dies
before it can call `ReleaseClaimAsync` at all — this fix doesn't replace the crash-recovery timeout,
it just stops a *non-crash* failure from quietly borrowing it.

**Honestly flagged gap left behind, not yet closed:** `ReleaseClaimAsync`'s `UPDATE` is itself
unconditional — it does not check that the claim it's clearing is still the one this exact poll
tick placed, unlike `GetPendingAsync`'s own claim statement. A single publish attempt hanging past
the full 2-minute stale threshold could let another replica reclaim and start reprocessing the same
row before this replica's own delayed release runs, freeing it for a third pickup mid-publish — the
same "unconditional mutation of shared state" shape this exact commit closed twice elsewhere
(`EntityCacheService`'s single-flight cleanup, `TrafficRunCoordinator`'s slot release). The
project's circuit breaker will usually trip and fail fast well before a single attempt hangs that
long under a real sustained outage, which is why this was judged non-blocking rather than fixed in
the same pass — see `.notes/review-reports/2026-11-04-2129-close-5-bugs-round-2.md`, F1 (still
`open` at the time of writing).

(see `.notes/review-reports/2026-11-04-2129-close-5-bugs-round-2.md` — not a numbered finding in that
report itself (all four code fixes were pre-identified by external review and confirmed by
inspection, not separately numbered findings), fixed in `5c8475b`)

## Relatives

### Nodes

- [LinkApi](node:link-api)
- [RedirectApi](node:redirect-api)

### Patterns

- [Async messaging](pattern:async-messaging) — the SQLite fallback queue this claim mechanism
  belongs to
