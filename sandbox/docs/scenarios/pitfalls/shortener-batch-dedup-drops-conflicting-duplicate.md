# Intra-batch same-hash dedup silently dropped a genuine conflict, not just a redelivery

**Category:** resilience-gap **Status:** fixed

A batch consumer that groups incoming messages by some natural key (here: the short-link hash) and
keeps only the last of each group is a common, cheap way to collapse redeliveries — but "same key
twice in one batch" can mean two very different things, and collapsing both the same way is only
safe as long as a second thing can never legitimately happen. Once this project added custom
aliases, a hash stopped being something only one real message could ever produce (a random hash's
keyspace makes a true collision astronomically unlikely) and became something two *different*
users' requests can legitimately race for. The dedup step kept treating every duplicate hash in a
batch as "the same message arriving twice," silently discarding every non-survivor — correct for a
redelivery, but wrong for a genuine conflict between two different payloads, with no poison
reporting at all for the dropped item. The gap mirrored one the same commit had just fixed at the
two other conflict-detection sites (the against-Postgres pre-check and the post-`DbUpdateException`
recovery) — but this third site was missed in the first pass, because grouping-and-collapsing reads
as "obviously the same thing twice" unless you specifically ask what else could land in the same
group. Watch for this any time a dedup/collapse step and a conflict-detection step both exist over
the same key: if the conflict check doesn't also run *inside* the dedup step, a conflict that
happens to resolve before the conflict-detection code ever sees it will vanish without a trace,
worse than if the dedup step didn't exist at all.

⚠️ **Mistake** — `src/backend/Services/ShortenerService/LinkCreatedConsumer.cs` (as shipped in
`bd2364f`, after the against-Postgres sites already got the content-comparison fix):

```csharp
// Same hash can appear twice in one batch if a redelivery lands alongside a fresher copy of
// itself - keep the last occurrence.
var distinctByHash = batch
    .GroupBy(x => x.Payload.Hash)
    .Select(g => g.Last())
    .ToList();
```

Every item in a group except `g.Last()` is simply dropped — no comparison against what's being
kept, no entry in `poisonMessages`. If two different alias-claiming messages for the same hash ever
landed in the same batch window, the loser would vanish with zero trace: no `409`/poison reaches
whoever sent it, nothing dead-lettered, nothing logged.

✅ **Do this instead** — walk each group explicitly and apply the same content comparison the other
two conflict-detection sites already use, so a dropped item is only ever dropped because it's
actually identical to the survivor:

```csharp
var distinctByHash = new List<BatchItem<LinkCreatedEvent>>();
foreach (var group in batch.GroupBy(x => x.Payload.Hash))
{
    var groupItems = group.ToList();
    var survivor = groupItems[^1];
    distinctByHash.Add(survivor);

    foreach (var loser in groupItems.SkipLast(1))
    {
        if (loser.Payload.OriginalLink == survivor.Payload.OriginalLink)
        {
            continue; // identical content - an intra-batch redelivery, not a conflict
        }

        poisonMessages.Add(new PoisonMessage(loser.MessageId,
            ConflictReason(loser.Payload.Hash, survivor.Payload.HasCustomAlias)));
    }
}
```

Not reachable today by construction, not just by luck — aliases are forced through the synchronous
gRPC path exclusively (always a batch of one), so the only way two items can currently share a hash
within one real RabbitMQ batch is a true redelivery of identical content, and a real random-hash
collision is practically impossible. The fix closes the gap anyway because nothing in the type
system enforces that invariant — if aliases were ever routed through the async/RabbitMQ path in a
future change, this would otherwise have been a live, silent data-loss bug from day one of that
change, not a new one introduced by it.

(see review-reports/2026-10-15-1042-custom-alias-creation.md, F2 — fixed in `365dff7`)

## Relatives

### Nodes

- [ShortenerService](node:shortener-service) — the consumer this dedup step lives in
- [LinkApi](node:link-api) — the only current producer of alias-flagged events, via the
  synchronous path that happens to make this gap unreachable today

### Patterns

- [Custom aliases](pattern:custom-aliases) — what turned "same hash twice" from an impossible case
  into a real one
- [Async messaging](pattern:async-messaging) — the batch-consumption mechanism this dedup step is
  part of
