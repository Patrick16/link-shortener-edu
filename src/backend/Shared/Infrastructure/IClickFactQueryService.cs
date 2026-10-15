using Common.Models;

namespace Infrastructure;

// Read side of the ClickHouse read model - ReportingApi is the only caller. Separate interface from
// IClickFactStore (not just separate implementation) so the two services only ever depend on the
// half of the capability they actually use - ReportingService can never accidentally query, and
// ReportingApi can never accidentally write.
public interface IClickFactQueryService : IPingable
{
    // null means "this hash has clicks recorded, but none owned by this caller" - ReportsController
    // turns that into a 404, same as an unknown hash, so a caller can't distinguish "not yours" from
    // "doesn't exist" (see ClickHouseClickFactQueryService's own comment on why that's not the same
    // thing as a hash with zero clicks at all, which returns an ordinary all-zero summary instead).
    Task<ClickSummary?> GetSummaryAsync(string hash, Guid callerUserId, CancellationToken cancellationToken = default);
}
