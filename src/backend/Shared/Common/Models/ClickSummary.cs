namespace Common.Models;

public record CountBucket(string Key, long Count);

// ReportingApi's read-side response shape - deliberately not reused by any Postgres/Mongo query,
// this is purely what the ClickHouse aggregate queries produce.
public record ClickSummary(
    string Hash,
    long TotalClicks,
    IReadOnlyList<CountBucket> ByDay,
    IReadOnlyList<CountBucket> ByCountry,
    IReadOnlyList<CountBucket> ByDevice,
    IReadOnlyList<CountBucket> ByBrowser);
