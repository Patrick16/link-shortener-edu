using Common;
using Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ShortenerService;

// TTL sweep: once a minute, marks every link whose ExpiresAt has passed as deleted. Runs inside
// ShortenerService (the process that already owns links_db) rather than publishing a
// LinkDeletedEvent for itself to consume - there is no cross-service hop to make, so going through
// RabbitMQ here would just be a slower, more failure-prone way of running an UPDATE it can already
// run directly. Manual delete (LinkApi -> LinkDeletedEvent -> LinkDeletedConsumer) is the only path
// that actually needs the bus, since that trigger originates in a different process.
public class LinkExpiryWorker(
    IConfiguration configuration,
    ILinkDeletionMarkerStore deletionMarkers,
    ILogger<LinkExpiryWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    // Same reasoning as PartitionMaintenanceWorker.AdvisoryLockName - a session-scoped advisory
    // lock, hashtext()'d from a stable name so every ShortenerService replica agrees on which
    // integer it's locking. The sweep's own UPDATE is idempotent (the WHERE guard below only ever
    // touches not-yet-deleted rows), so an uncoordinated race wouldn't corrupt data, but it would
    // have every replica hammer the same rows and double-confirm the same tombstones every tick.
    private const string AdvisoryLockName = "shortener-service:link-expiry";

    private readonly IConfiguration _configuration = configuration;
    private readonly ILinkDeletionMarkerStore _deletionMarkers = deletionMarkers;
    private readonly ILogger<LinkExpiryWorker> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Runs once immediately rather than waiting for the first tick - a stack that was down for
        // a while shouldn't leave already-expired links resolvable for up to another minute after
        // startup.
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
            await RunOnceAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Same guard as PartitionMaintenanceWorker.TickAsync - a transient failure here (primary
            // briefly unreachable) must not crash the whole host.
            _logger.LogWarning(ex, "Link expiry sweep tick failed - will retry next tick");
        }
    }

    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        // Direct connection, bypassing pgcat - same reasoning as PartitionMaintenanceWorker's own
        // DbContextOptionsBuilder: the advisory lock below is session-scoped (tied to one physical
        // Postgres connection), and pgcat's transaction-pooling mode gives no guarantee that two
        // commands from the same logical client land on the same physical connection.
        var connectionString = _configuration.GetConnectionString(Constants.PostgresPrimaryConnectionString)
            ?? _configuration.GetConnectionString(Constants.PostgresConnectionString);
        var options = new DbContextOptionsBuilder<DatabaseContext>().UseNpgsql(connectionString).Options;
        await using var context = new DatabaseContext(options);

        // Pins one physical connection for the whole acquire/work/release section - see
        // PartitionMaintenanceWorker.RunOnceAsync's own comment on OpenConnectionAsync for the
        // silent-permanent-self-deadlock this prevents.
        await context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            if (!await TryAcquireAdvisoryLockAsync(context, cancellationToken))
            {
                _logger.LogDebug("Link expiry sweep: another replica holds the lock, skipping this tick");
                return;
            }

            try
            {
                // RETURNING, not a separate SELECT-then-UPDATE: tells us exactly which hashes this
                // tick actually flipped, in case a manual DELETE for the same hash committed between
                // a hypothetical select and this statement - the DeletedAt IS NULL guard means a
                // hash manual-deleted first is simply not among the rows returned here, so it never
                // gets double-processed.
                var expiredHashes = await context.Database.SqlQueryRaw<string>(
                    """
                    UPDATE links SET "DeletedAt" = now()
                    WHERE "ExpiresAt" IS NOT NULL AND "ExpiresAt" <= now() AND "DeletedAt" IS NULL
                    RETURNING "Hash" AS "Value"
                    """).ToListAsync(cancellationToken);

                var now = DateTime.UtcNow;
                foreach (var hash in expiredHashes)
                {
                    await _deletionMarkers.ConfirmAsync(hash, DeletedReason.Expired, now, cancellationToken);
                }

                if (expiredHashes.Count > 0)
                {
                    _logger.LogInformation("Link expiry sweep: expired {Count} link(s)", expiredHashes.Count);
                }
                else
                {
                    _logger.LogDebug("Link expiry sweep: nothing expired this tick");
                }
            }
            finally
            {
                await ReleaseAdvisoryLockAsync(context, cancellationToken);
            }
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    private static Task<bool> TryAcquireAdvisoryLockAsync(DatabaseContext context, CancellationToken cancellationToken) =>
        context.Database.SqlQueryRaw<bool>($"SELECT pg_try_advisory_lock(hashtext('{AdvisoryLockName}')) AS \"Value\"").SingleAsync(cancellationToken);

    private static Task<int> ReleaseAdvisoryLockAsync(DatabaseContext context, CancellationToken cancellationToken) =>
        context.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_unlock(hashtext('{AdvisoryLockName}'))", cancellationToken);
}
