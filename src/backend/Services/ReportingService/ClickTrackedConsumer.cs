using Common;
using Common.Models;
using Contracts.Events;
using Infrastructure;

namespace ReportingService;

// Second consumer of the same click.tracked topic TrafficService already consumes (fan-out, not a
// chain - RedirectApi's publish side is untouched) - this is the CQRS read side, building its own
// denormalized ClickFact rows in ClickHouse straight from the raw event, independently of
// TrafficService's Postgres/Mongo writes. Uses its own queue name so RabbitMQ delivers every
// message to both consumers instead of the two competing for the same deliveries.
public sealed class ClickTrackedConsumer(
    IMessageConsumer consumer,
    IClickFactStore clickFactStore,
    IUserAgentParser userAgentParser,
    IGeoIpResolver geoIpResolver,
    ILogger<ClickTrackedConsumer> logger) : BackgroundService
{
    private const string QueueName = "reporting-service.click-tracked";

    // See TrafficService.ClickTrackedConsumer's identical constant/reasoning - ip-api.com's free
    // tier caps at ~45 requests/minute, bounded parallelism overlaps most of a batch's latency
    // without hammering the rate limit any harder than real traffic already would.
    private const int GeoIpConcurrency = 8;

    private readonly IMessageConsumer _consumer = consumer;
    private readonly IClickFactStore _clickFactStore = clickFactStore;
    private readonly IUserAgentParser _userAgentParser = userAgentParser;
    private readonly IGeoIpResolver _geoIpResolver = geoIpResolver;
    private readonly ILogger<ClickTrackedConsumer> _logger = logger;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        _consumer.ConsumeBatchAsync<ClickTrackedEvent>(QueueName, Topics.ClickTracked, HandleBatchAsync, stoppingToken);

    // ClickHouse table uses ReplacingMergeTree ORDER BY (hash, id) - see
    // sandbox/infra/clickhouse/init.sql - so a redelivered event (same Id, same Hash) eventually
    // collapses to one row during a background merge, without this consumer needing to query
    // ClickHouse first to check what's already there (an expensive point-lookup pattern ClickHouse
    // is specifically bad at). Dedup here is only within-batch, matching TrafficService's own
    // reasoning for the same "redelivery landed alongside a fresher copy of itself" case.
    internal async Task<BatchOutcome> HandleBatchAsync(
        IReadOnlyList<BatchItem<ClickTrackedEvent>> batch, CancellationToken cancellationToken)
    {
        var distinctById = batch
            .Select(x => x.Payload)
            .GroupBy(x => x.Id)
            .Select(g => g.Last())
            .ToList();

        var geoResults = new string?[distinctById.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, distinctById.Count),
            new ParallelOptions { MaxDegreeOfParallelism = GeoIpConcurrency, CancellationToken = cancellationToken },
            async (i, ct) => geoResults[i] = (await _geoIpResolver.ResolveAsync(distinctById[i].IpAddress, ct)).Country);

        var facts = new List<ClickFact>(distinctById.Count);
        for (var i = 0; i < distinctById.Count; i++)
        {
            var e = distinctById[i];
            var parsedUserAgent = _userAgentParser.Parse(e.UserAgent);
            facts.Add(new ClickFact(
                e.Id,
                e.Hash,
                e.ClickedAt,
                geoResults[i],
                parsedUserAgent.DeviceType,
                parsedUserAgent.Browser,
                parsedUserAgent.Os,
                ExtractReferrerDomain(e.Referrer)));
        }

        await _clickFactStore.InsertManyAsync(facts, cancellationToken);

        _logger.LogInformation("Inserted {Count} click fact(s) from this batch into ClickHouse", facts.Count);
        return BatchOutcome.Success;
    }

    private static string? ExtractReferrerDomain(string referrer) =>
        !string.IsNullOrWhiteSpace(referrer) && Uri.TryCreate(referrer, UriKind.Absolute, out var uri)
            ? uri.Host
            : null;
}
