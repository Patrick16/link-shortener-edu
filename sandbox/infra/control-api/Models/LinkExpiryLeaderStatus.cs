namespace ControlApi.Models;

// Read straight off Redis (GET + PTTL on the lease key LinkExpiryWorker itself claims/renews) -
// not cached or derived from architecture.json, since leadership can change with zero involvement
// from this app the moment a replica dies and another one's claim attempt wins. Leader is null
// (and LeaseTtlMs with it) when nothing currently holds the lease, e.g. right after every
// ShortenerService replica was stopped.
public record LinkExpiryLeaderStatus(string? Leader, long? LeaseTtlMs);
