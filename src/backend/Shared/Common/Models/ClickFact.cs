namespace Common.Models;

// The CQRS read-side's own flattened shape for a click, written once by ReportingService and never
// updated - deliberately not the same type as Click (clicks_db) or ClickMeta (clicks_meta_db): this
// is a denormalized fact row for OLAP aggregation, not a transactional record or a flexible
// document. Only the columns reports actually group/filter by are kept; raw UserAgent/Referrer
// strings stay in Mongo's ClickMeta, not duplicated here.
public record ClickFact(
    Guid Id,
    string Hash,
    DateTime ClickedAt,
    string? Country,
    string? DeviceType,
    string? Browser,
    string? Os,
    string? ReferrerDomain);
