using Common.Models;

namespace Infrastructure;

// Write side of the ClickHouse read model - ReportingService is the only caller, matching the
// "one owning service per database" rule the rest of this codebase follows for Postgres/Mongo.
public interface IClickFactStore : IPingable
{
    // Always a bulk write - see ClickHouseClickFactStore for why a single-row equivalent isn't offered.
    Task InsertManyAsync(IReadOnlyCollection<ClickFact> facts, CancellationToken cancellationToken = default);
}
