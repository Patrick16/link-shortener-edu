using Common;
using Common.Models;
using Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace ShortenerService;

// TTL sweep: once a minute, marks every link whose ExpiresAt has passed as deleted. Runs inside
// ShortenerService (the process that already owns links_db) rather than publishing a
// LinkDeletedEvent for itself to consume - there is no cross-service hop to make, so going through
// RabbitMQ here would just be a slower, more failure-prone way of running an UPDATE it can already
// run directly. Manual delete (LinkApi -> LinkDeletedEvent -> LinkDeletedConsumer) is the only path
// that actually needs the bus, since that trigger originates in a different process.
//
// Only the elected leader among ShortenerService's replicas actually sweeps - a sustained Redis
// lease (SET NX PX to acquire, a renew script that only extends the TTL if the value is still
// ours), not PartitionMaintenanceWorker's non-blocking Postgres advisory lock re-acquired fresh
// every tick. Both solve the identical problem (one singleton job across N competing replicas);
// this worker deliberately uses the other mechanism so the two are a real side-by-side comparison -
// see pattern:distributed-locking for the writeup. One side effect of the switch: this worker no
// longer needs a direct-to-primary, pgcat-bypassing connection at all (that requirement was only
// ever about the Postgres advisory lock being session-scoped) - the sweep itself now goes through
// the normal pooled IDbContextFactory, same as ClickCountFlushWorker.
public class LinkExpiryWorker(
    IDbContextFactory<DatabaseContext> dbContextFactory,
    IConnectionMultiplexer redis,
    ILinkDeletionMarkerStore deletionMarkers,
    IEntityCacheService<Link> cache,
    ILogger<LinkExpiryWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    // Independent of Interval on purpose: the lease has to be renewed much faster than the sweep
    // itself runs, or a leader that's still alive but simply hasn't hit its once-a-minute tick yet
    // would look dead to everyone else. LeaseDuration is 3x RenewInterval - one missed renew (a
    // slow Redis round trip, a GC pause) doesn't yet cost the leadership.
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RenewInterval = TimeSpan.FromSeconds(5);
    private const string LeaderKey = "leader:shortener-service:link-expiry";

    // Lua, not GET-then-PEXPIRE from the client: a leader must only ever extend a lease that still
    // names IT as the holder. Without this check atomically bundled with the extend, a leader whose
    // lease expired a moment ago (e.g. it stalled past LeaseDuration) could blindly PEXPIRE a key
    // some other replica has since legitimately claimed, stealing leadership back out from under the
    // new, genuinely-alive leader. Same embedded-script idiom RedisRateLimiter already uses for its
    // own atomic increment-and-maybe-expire.
    private const string RenewLeaseScript = """
        if redis.call("GET", KEYS[1]) == ARGV[1] then
            return redis.call("PEXPIRE", KEYS[1], ARGV[2])
        else
            return 0
        end
        """;

    // The container's own hostname - unique per replica under `docker compose up --scale`, and
    // directly actionable for a kill-and-watch failover demo (it's exactly what `docker kill`
    // takes), unlike an opaque GUID that would need a separate lookup to map back to a container.
    private readonly string _replicaId = Environment.MachineName;

    private readonly IDbContextFactory<DatabaseContext> _dbContextFactory = dbContextFactory;
    private readonly IConnectionMultiplexer _redis = redis;
    private readonly ILinkDeletionMarkerStore _deletionMarkers = deletionMarkers;
    private readonly IEntityCacheService<Link> _cache = cache;
    private readonly ILogger<LinkExpiryWorker> _logger = logger;

    // Read by the (much more frequent) leader-election loop, read by the sweep loop - volatile
    // because nothing else coordinates the handoff between those two concurrently running loops.
    private volatile bool _isLeader;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(RunLeaderElectionLoopAsync(stoppingToken), RunExpirySweepLoopAsync(stoppingToken));

    private async Task RunLeaderElectionLoopAsync(CancellationToken stoppingToken)
    {
        // Attempt a claim immediately rather than waiting a full RenewInterval first - otherwise a
        // freshly started replica (e.g. the only one left after the old leader was killed) would
        // sit idle for up to RenewInterval before even trying to take over.
        await LeaderElectionTickAsync(stoppingToken);

        using var timer = new PeriodicTimer(RenewInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await LeaderElectionTickAsync(stoppingToken);
        }
    }

    internal async Task<bool> LeaderElectionTickAsync(CancellationToken cancellationToken)
    {
        try
        {
            var db = _redis.GetDatabase();
            var wasLeader = _isLeader;
            _isLeader = wasLeader
                ? (long)await db.ScriptEvaluateAsync(RenewLeaseScript, [(RedisKey)LeaderKey], [(RedisValue)_replicaId, (RedisValue)(long)LeaseDuration.TotalMilliseconds]) == 1
                : await db.StringSetAsync(LeaderKey, _replicaId, LeaseDuration, When.NotExists);

            if (_isLeader && !wasLeader)
            {
                _logger.LogInformation("Acquired link-expiry leadership ({ReplicaId})", _replicaId);
            }
            else if (!_isLeader && wasLeader)
            {
                _logger.LogInformation("Lost link-expiry leadership ({ReplicaId}) - lease was not renewed in time", _replicaId);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail SAFE here, not fail open like TopLinksPushService's push claim: unlike a missed
            // push (one stale dashboard update), an uncoordinated sweep means every replica issuing
            // the same UPDATE on every tick the moment Redis comes back - wasted, not incorrect
            // (the UPDATE's own WHERE guard is idempotent either way), but still the wrong default
            // to reach for just because Redis had a blip.
            _logger.LogWarning(ex, "Leader election check failed for link-expiry - assuming not leader");
            _isLeader = false;
        }

        return _isLeader;
    }

    private async Task RunExpirySweepLoopAsync(CancellationToken stoppingToken)
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
            _logger.LogWarning(ex, "Link expiry sweep tick failed - will retry next tick");
        }
    }

    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        if (!_isLeader)
        {
            _logger.LogDebug("Link expiry sweep: not the elected leader, skipping this tick");
            return;
        }

        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        // RETURNING, not a separate SELECT-then-UPDATE: tells us exactly which hashes this tick
        // actually flipped, in case a manual DELETE for the same hash committed between a
        // hypothetical select and this statement - the DeletedAt IS NULL guard means a hash
        // manual-deleted first is simply not among the rows returned here, so it never gets
        // double-processed.
        var expiredHashes = await context.Database.SqlQueryRaw<string>(
            """
            UPDATE links SET "DeletedAt" = now()
            WHERE "ExpiresAt" IS NOT NULL AND "ExpiresAt" <= now() AND "DeletedAt" IS NULL
            RETURNING "Hash" AS "Value"
            """).ToListAsync(cancellationToken);

        // Also invalidates the main link:{hash} cache-aside entry, not just the tombstone - see
        // LinkDeletedConsumer's identical comment (found in review) for why relying on the
        // tombstone alone left a cached-before-expiry Link resolving successfully for up to its
        // full cache TTL if the tombstone write specifically failed.
        var now = DateTime.UtcNow;
        foreach (var hash in expiredHashes)
        {
            await _deletionMarkers.ConfirmAsync(hash, DeletedReason.Expired, now, cancellationToken);
            await _cache.InvalidateCacheAsync(hash);
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
}
