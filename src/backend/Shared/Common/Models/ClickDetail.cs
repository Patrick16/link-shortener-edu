namespace Common.Models;

// ReportingApi's raw per-click read shape (GET /reports/{hash}/clicks), backed directly by Mongo's
// ClickMeta documents - the flexible-document counterpart to ClickSummary's pre-aggregated
// ClickHouse buckets: individual rows with the full fidelity ClickFact's rollup deliberately drops
// (exact City, full UserAgent string, Referrer URL), instead of counts grouped by column.
//
// Deliberately excludes IpAddress: ClickMeta keeps it, but handing a visitor's raw IP to the link's
// owner through this API is a PII exposure this feature doesn't need to take on.
public record ClickDetail(
    Guid Id,
    DateTime ClickedAt,
    string UserAgent,
    string Referrer,
    string? Browser,
    string? Os,
    string? DeviceType,
    string? Country,
    string? City);
