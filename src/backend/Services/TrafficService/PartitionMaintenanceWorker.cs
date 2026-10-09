using Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TrafficService;

// Keeps the clicks table's monthly partitions rolling: creates partitions far enough ahead that
// writes never fall through to the slower clicks_default catch-all in normal operation, and drops
// partitions once they're fully past the retention window - the maintenance job
// ConvertClicksToPartitionedTable's own bootstrap explicitly left for later (see that migration's
// comment and .notes/PLAN.md's Feature: Data Retention).
public class PartitionMaintenanceWorker(
    IConfiguration configuration,
    ILogger<PartitionMaintenanceWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    // Matches the bootstrap migration's own "3 months ahead" window.
    private const int LookAheadMonths = 3;
    // Matches MongoClickMetaStore's TTL index / ClickHouse's reports_db.clicks TTL (~90 days),
    // rounded to whole months - see PartitionPlanner.GetPartitionsToDrop's own comment.
    private const int RetentionMonths = 3;
    // Postgres advisory locks are session-scoped integers, not strings - hashtext() is the
    // standard way to turn a stable name into one. Needed because traffic-service is a scalable
    // service (see ScalableServices in control-api): with N replicas all running this same timer,
    // an uncoordinated tick would have every replica try the same CREATE/DROP TABLE at once. The
    // DDL itself is idempotent (IF NOT EXISTS / IF EXISTS) so a race wouldn't corrupt anything,
    // but it would spam duplicate log lines and duplicate DDL attempts for no benefit - the lock
    // just makes "one replica does it, the rest skip this tick" the normal case instead of luck.
    private const string AdvisoryLockName = "traffic-service:partition-maintenance";

    private readonly IConfiguration _configuration = configuration;
    private readonly ILogger<PartitionMaintenanceWorker> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Runs once immediately rather than waiting for the first PeriodicTimer tick - a stack
        // that's been down across a month boundary shouldn't wait up to 24h to catch up.
        await TickAsync(stoppingToken);

        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await TickAsync(stoppingToken);
        }
    }

    private async Task TickAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RunOnceAsync(DateTime.UtcNow, stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A transient failure here (primary briefly unreachable) must not crash the whole
            // host - same reasoning as RabbitMqRetryWorker's identical guard.
            _logger.LogWarning(ex, "Partition maintenance tick failed - will retry next tick");
        }
    }

    internal async Task RunOnceAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        // Bypasses pgcat entirely, same as MigratePostgresAsync - DDL through a query-classifying
        // pooler is inherently fragile (see the pgcat-missing-read-write-split pitfall), and every
        // statement this job runs is DDL.
        var connectionString = _configuration.GetConnectionString(Constants.PostgresPrimaryConnectionString)
            ?? _configuration.GetConnectionString(Constants.PostgresConnectionString);
        var options = new DbContextOptionsBuilder<DatabaseContext>().UseNpgsql(connectionString).Options;
        await using var context = new DatabaseContext(options);

        if (!await TryAcquireAdvisoryLockAsync(context, cancellationToken))
        {
            _logger.LogDebug("Partition maintenance: another replica holds the lock, skipping this tick");
            return;
        }

        try
        {
            var existingPartitions = await ListPartitionsAsync(context, cancellationToken);

            // EF1002 flags interpolated-string SQL as injection risk, but a table/partition name
            // can never be parameterized by ANY SQL client - identifiers aren't values. This is
            // the exact same constraint ConvertClicksToPartitionedTable's migration worked around
            // with Postgres's own format()/%I. Safe here because every interpolated value is
            // server-computed by PartitionPlanner from DateOnly math (clicks_yYYYYmMM / ISO date
            // strings) - never user input, never anything that reaches this method from outside
            // the process.
#pragma warning disable EF1002
            var toCreate = PartitionPlanner.GetPartitionsToEnsure(nowUtc, LookAheadMonths)
                .Where(p => !existingPartitions.Contains(p.Name, StringComparer.Ordinal))
                .ToList();
            foreach (var partition in toCreate)
            {
                // IF NOT EXISTS is a second safety net on top of the advisory lock (e.g. a
                // partition created by hand between the list above and this statement) - belt and
                // suspenders, not load-bearing on its own.
                await context.Database.ExecuteSqlRawAsync(
                    $"""
                    CREATE TABLE IF NOT EXISTS "{partition.Name}" PARTITION OF clicks
                    FOR VALUES FROM ('{partition.Start:yyyy-MM-dd}') TO ('{partition.End:yyyy-MM-dd}')
                    """,
                    cancellationToken);
            }

            var toDrop = PartitionPlanner.GetPartitionsToDrop(nowUtc, RetentionMonths, existingPartitions);
            foreach (var name in toDrop)
            {
                await context.Database.ExecuteSqlRawAsync($"""DROP TABLE IF EXISTS "{name}" """, cancellationToken);
            }
#pragma warning restore EF1002

            if (toCreate.Count > 0 || toDrop.Count > 0)
            {
                _logger.LogInformation(
                    "Partition maintenance: created {CreatedCount} partition(s) [{Created}], dropped {DroppedCount} partition(s) [{Dropped}]",
                    toCreate.Count,
                    string.Join(", ", toCreate.Select(p => p.Name)),
                    toDrop.Count,
                    string.Join(", ", toDrop));
            }
            else
            {
                _logger.LogDebug("Partition maintenance: nothing to create or drop this tick");
            }
        }
        finally
        {
            await ReleaseAdvisoryLockAsync(context, cancellationToken);
        }
    }

    // EF Core's scalar SqlQueryRaw<T> requires the result set to have exactly one column, named
    // "Value" - undocumented in the method signature itself, only in the result-mapping
    // convention; an unaliased column (pg_try_advisory_lock's own function-name column) throws
    // "column s.Value does not exist" at the EF-generated wrapper-query level, not at the
    // hand-written SQL level, which is why `docker compose up` under -t for a quick syntax check
    // wouldn't have caught it - it only surfaces when the query actually runs.
    private static Task<bool> TryAcquireAdvisoryLockAsync(DatabaseContext context, CancellationToken cancellationToken) =>
        context.Database.SqlQueryRaw<bool>($"SELECT pg_try_advisory_lock(hashtext('{AdvisoryLockName}')) AS \"Value\"").SingleAsync(cancellationToken);

    private static Task<int> ReleaseAdvisoryLockAsync(DatabaseContext context, CancellationToken cancellationToken) =>
        context.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_unlock(hashtext('{AdvisoryLockName}'))", cancellationToken);

    // pg_inherits is how Postgres tracks partition membership - every child of the `clicks`
    // parent, by name, regardless of what created it (the bootstrap migration, an earlier tick of
    // this same job, or someone creating one by hand).
    private static Task<List<string>> ListPartitionsAsync(DatabaseContext context, CancellationToken cancellationToken) =>
        context.Database.SqlQueryRaw<string>(
            """
            SELECT child.relname AS "Value"
            FROM pg_inherits
            JOIN pg_class parent ON pg_inherits.inhparent = parent.oid
            JOIN pg_class child ON pg_inherits.inhrelid = child.oid
            WHERE parent.relname = 'clicks'
            """).ToListAsync(cancellationToken);
}
