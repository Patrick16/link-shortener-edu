# A read immediately after a write can land on a lagging replica

**Category:** race-condition **Status:** known limitation

PgCat's auto-routing treats every `SELECT` the same way regardless of what just happened on the
same connection, the same HTTP request, or even the same millisecond — it can send a read to a
replica that hasn't yet streamed the write that same logical operation just made. Streaming
replication lag is normally small, but it is never zero, and under load (or a momentarily slow
replica) the window is wide enough to matter.

**Concrete failure scenario in this project:** create a link (`POST /Links`, returns synchronously
once `LinkApi` has generated the hash and published the event — see
[Async messaging](pattern:async-messaging)), then immediately resolve it
(`GET /{hash}` on `RedirectApi`). If the read lands on a replica that hasn't caught up yet — or
even on the primary, before `ShortenerService` has finished consuming the event and persisting the
row — the lookup 404s even though the link "exists" from the caller's point of view. This is the
same shape as the pre-existing create→resolve eventual-consistency gap the async publish/consume
boundary already has, compounded by replica lag being a second source of the same kind of delay.

This hasn't been fixed, and fixing it isn't free: the usual options (routing a read-after-write to
the primary for some window, session-level consistency tokens, synchronous replication for
affected queries) all give back some of what read-replica routing was built to gain in the first
place. Worth demonstrating deliberately in the sandbox before it gets "fixed" one way or another —
see the open question already tracked for this.

(not yet fixed — tracked as an open question: whether to demo the read-your-writes problem on
purpose in the sandbox, or to skip it)

## Relatives

### Nodes

- [PgCat](node:pgcat)

### Patterns

- [Read-replica routing](pattern:read-replica-routing)
