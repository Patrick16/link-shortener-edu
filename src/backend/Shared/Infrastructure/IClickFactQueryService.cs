using Common.Models;

namespace Infrastructure;

// Read side of the ClickHouse read model - ReportingApi is the only caller. Separate interface from
// IClickFactStore (not just separate implementation) so the two services only ever depend on the
// half of the capability they actually use - ReportingService can never accidentally query, and
// ReportingApi can never accidentally write.
public interface IClickFactQueryService : IPingable
{
    // Every query backing this is filtered to callerUserId - a hash with zero clicks at all, a
    // hash this caller genuinely owns, and a hash owned by someone else all come back as an
    // ordinary (possibly all-zero) summary. Never null: there is deliberately no separate "not
    // yours" branch for ReportsController to turn into a 404, since a 404-only-for-someone-else's-
    // real-hash response is itself the leak - it would let a caller tell "this hash exists and
    // belongs to someone else" apart from "this hash doesn't exist" (found in review).
    Task<ClickSummary> GetSummaryAsync(string hash, Guid callerUserId, CancellationToken cancellationToken = default);
}
