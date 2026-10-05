using Common.Models;

namespace Infrastructure;

// Read side of the ClickHouse read model - ReportingApi is the only caller. Separate interface from
// IClickFactStore (not just separate implementation) so the two services only ever depend on the
// half of the capability they actually use - ReportingService can never accidentally query, and
// ReportingApi can never accidentally write.
public interface IClickFactQueryService : IPingable
{
    Task<ClickSummary> GetSummaryAsync(string hash, CancellationToken cancellationToken = default);
}
