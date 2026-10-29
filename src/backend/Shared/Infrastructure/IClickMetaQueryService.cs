using Common.Models;

namespace Infrastructure;

// Read side of clicks_meta_db, queried only by ReportingApi - separate interface from
// IClickMetaStore (not just separate implementation) for the same reason IClickFactQueryService is
// split from IClickFactStore: TrafficService (the writer) can never accidentally query, and
// ReportingApi (the reader) can never accidentally write.
public interface IClickMetaQueryService : IPingable
{
    // Owner-scoped the same way IClickFactQueryService.GetSummaryAsync is: filtered on both hash
    // AND callerUserId, so a hash with no clicks, a hash the caller genuinely owns, and a hash
    // owned by someone else (or clicked before ClickMeta.UserId existed) all come back as an
    // ordinary, possibly empty list - no separate branch that would let a caller distinguish
    // "exists, not yours" from "doesn't exist".
    Task<IReadOnlyList<ClickDetail>> GetRecentAsync(
        string hash, Guid callerUserId, int limit, CancellationToken cancellationToken = default);
}
