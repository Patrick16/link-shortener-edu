using Common;
using Common.Models;
using Contracts.Events;
using Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace TrafficService;

// Consumes batches of ClickTrackedEvent and persists them to Postgres Clicks (the core click
// record) and ClickMeta documents in Mongo (browser/OS/device/geo — schema-less, grows
// independently of the core record). The two writes aren't transactional with each other, so
// redelivery has to handle each independently: every event carries its own Id (generated once by
// RedirectApi), so a batch containing an already-persisted Id skips only that Postgres insert
// (avoiding the primary-key violation) but still goes on to attempt every click's Mongo write - that
// write is itself idempotent (upsert by Id, see MongoClickMetaStore), so retrying it is always safe,
// and skipping it just because Postgres already has the row would otherwise mean a redelivery caused
// by a transient Mongo failure permanently loses that click's metadata.
public sealed class ClickTrackedConsumer(
    IMessageConsumer consumer,
    IDbContextFactory<DatabaseContext> dbContextFactory,
    IClickMetaStore clickMetaStore,
    IUserAgentParser userAgentParser,
    IGeoIpResolver geoIpResolver,
    ILogger<ClickTrackedConsumer> logger) : BackgroundService
{
    private const string QueueName = "traffic-service.click-tracked";

    // ip-api.com's free tier caps at ~45 requests/minute per caller IP - resolving a whole batch
    // (default 100 clicks) one at a time serializes up to batch-size * the resolver's own 3s
    // timeout behind the Mongo write below. A bounded degree of parallelism overlaps most of that
    // latency without hammering the rate limit any harder than a burst of real traffic already would.
    private const int GeoIpConcurrency = 8;

    private readonly IMessageConsumer _consumer = consumer;
    private readonly IDbContextFactory<DatabaseContext> _dbContextFactory = dbContextFactory;
    private readonly IClickMetaStore _clickMetaStore = clickMetaStore;
    private readonly IUserAgentParser _userAgentParser = userAgentParser;
    private readonly IGeoIpResolver _geoIpResolver = geoIpResolver;
    private readonly ILogger<ClickTrackedConsumer> _logger = logger;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        _consumer.ConsumeBatchAsync<ClickTrackedEvent>(QueueName, Topics.ClickTracked, HandleBatchAsync, stoppingToken);

    internal async Task<BatchOutcome> HandleBatchAsync(
        IReadOnlyList<BatchItem<ClickTrackedEvent>> batch, CancellationToken cancellationToken)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        // The same click Id can appear twice in one batch if a redelivery lands alongside a fresher
        // copy of itself - keep the last occurrence, same as "process it once" for any other duplicate.
        var distinctByClickId = batch
            .Select(x => x.Payload)
            .GroupBy(x => x.Id)
            .Select(g => g.Last())
            .ToList();

        var ids = distinctByClickId.Select(x => x.Id).ToList();
        var alreadyStored = await context.Clicks
            .Where(x => ids.Contains(x.Id))
            .Select(x => x.Id)
            .ToHashSetAsync(cancellationToken);

        var newClicks = distinctByClickId.Where(x => !alreadyStored.Contains(x.Id)).ToList();
        context.Clicks.AddRange(newClicks.Select(e => new Click(e.Id, e.ClickedAt, e.InboundLink, e.OutboundLink, e.Hash)));
        await context.SaveChangesAsync(cancellationToken);

        if (alreadyStored.Count > 0)
        {
            _logger.LogInformation(
                "{Count} click(s) in this batch were already persisted in Postgres - skipped their inserts, " +
                "but still re-attempting the Mongo ClickMeta write for them in case an earlier attempt failed",
                alreadyStored.Count);
        }

        // Geo-IP resolution is the only per-item cost worth overlapping here - UserAgent parsing is
        // pure in-process regex work, cheap enough to stay sequential below.
        var geoResults = new GeoLocation[distinctByClickId.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, distinctByClickId.Count),
            new ParallelOptions { MaxDegreeOfParallelism = GeoIpConcurrency, CancellationToken = cancellationToken },
            async (i, ct) => geoResults[i] = await _geoIpResolver.ResolveAsync(distinctByClickId[i].IpAddress, ct));

        var metas = new List<ClickMeta>(distinctByClickId.Count);
        for (var i = 0; i < distinctByClickId.Count; i++)
        {
            var e = distinctByClickId[i];
            var parsedUserAgent = _userAgentParser.Parse(e.UserAgent);
            var geo = geoResults[i];
            metas.Add(new ClickMeta(
                e.Id,
                e.Hash,
                e.ClickedAt,
                e.UserAgent,
                e.Referrer,
                e.IpAddress,
                parsedUserAgent.Browser,
                parsedUserAgent.Os,
                parsedUserAgent.DeviceType,
                geo.Country,
                geo.City));
        }

        await _clickMetaStore.SaveManyAsync(metas, cancellationToken);

        _logger.LogInformation("Processed {Count} clicks ({AlreadyStored} already stored)", distinctByClickId.Count, alreadyStored.Count);
        return BatchOutcome.Success;
    }
}
