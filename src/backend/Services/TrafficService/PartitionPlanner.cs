namespace TrafficService;

// Pure date/name math for the clicks table's rolling partitions - no I/O, so it's fully unit
// testable without a real Postgres instance. Native partitioning (PARTITION BY RANGE, pg_inherits)
// has no InMemory/SQLite equivalent to test against - same reason
// ConvertClicksToPartitionedTable itself has no automated test, only live verification (see that
// migration's own comment); keeping the decision logic here, separate from the DDL execution in
// PartitionMaintenanceWorker, is what makes this part testable despite that.
public static class PartitionPlanner
{
    private const string PartitionPrefix = "clicks_y";
    private const string DefaultPartitionName = "clicks_default";

    public readonly record struct PartitionRange(string Name, DateOnly Start, DateOnly End);

    // Every partition that should exist for "now" through lookAheadMonths months ahead, inclusive
    // of the current month - same clicks_yYYYYmMM naming and FOR VALUES FROM/TO shape as the
    // bootstrap migration, just computed fresh on every tick instead of once at migration time.
    public static IReadOnlyList<PartitionRange> GetPartitionsToEnsure(DateTime nowUtc, int lookAheadMonths)
    {
        var currentMonth = new DateOnly(nowUtc.Year, nowUtc.Month, 1);
        var result = new List<PartitionRange>(lookAheadMonths + 1);
        for (var i = 0; i <= lookAheadMonths; i++)
        {
            var start = currentMonth.AddMonths(i);
            var end = start.AddMonths(1);
            result.Add(new PartitionRange($"{PartitionPrefix}{start:yyyy}m{start:MM}", start, end));
        }
        return result;
    }

    // Which of the currently existing partitions (read from pg_inherits by the caller) are
    // entirely past the retention window and safe to drop. clicks_default is never a candidate -
    // it's the permanent catch-all, not a dated partition - and anything that doesn't match the
    // clicks_yYYYYmMM shape is left alone rather than guessed at: a differently-named partition
    // someone created by hand shouldn't get silently dropped by this job.
    //
    // retentionMonths rounds UP to whole months (Postgres can only drop whole partitions) - keeps
    // the current month plus (retentionMonths - 1) months before it, e.g. retentionMonths=3 on
    // 2026-10-09 keeps Aug/Sep/Oct and drops anything that ended by 2026-08-01. This is the same
    // ~90-day horizon MongoClickMetaStore's TTL index and ClickHouse's reports_db.clicks TTL use,
    // just quantized to month boundaries instead of a continuously-rolling cutoff.
    public static IReadOnlyList<string> GetPartitionsToDrop(
        DateTime nowUtc, int retentionMonths, IEnumerable<string> existingPartitionNames)
    {
        var cutoff = new DateOnly(nowUtc.Year, nowUtc.Month, 1).AddMonths(-(retentionMonths - 1));
        var toDrop = new List<string>();
        foreach (var name in existingPartitionNames)
        {
            if (name == DefaultPartitionName) continue;
            if (!TryParsePartitionEnd(name, out var end)) continue;
            if (end <= cutoff) toDrop.Add(name);
        }
        return toDrop;
    }

    private static bool TryParsePartitionEnd(string name, out DateOnly end)
    {
        end = default;
        // clicks_yYYYYmMM - exactly this shape (8-char prefix + 4-digit year + 'm' + 2-digit
        // month), nothing looser accepted.
        const int expectedLength = 8 /* "clicks_y" */ + 4 /* YYYY */ + 1 /* 'm' */ + 2 /* MM */;
        if (name.Length != expectedLength || !name.StartsWith(PartitionPrefix, StringComparison.Ordinal))
            return false;

        var yearSpan = name.AsSpan(PartitionPrefix.Length, 4);
        var monthMarker = name[PartitionPrefix.Length + 4];
        var monthSpan = name.AsSpan(PartitionPrefix.Length + 5, 2);
        if (monthMarker != 'm'
            || !int.TryParse(yearSpan, out var year)
            || !int.TryParse(monthSpan, out var month)
            || month is < 1 or > 12)
        {
            return false;
        }

        end = new DateOnly(year, month, 1).AddMonths(1);
        return true;
    }
}
