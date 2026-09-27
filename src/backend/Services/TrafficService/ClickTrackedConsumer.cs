using Common;
using Common.Models;
using Contracts.Events;
using Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace TrafficService;

// Consumes ClickTrackedEvent and persists it to Postgres Clicks (the core click record) and a
// ClickMeta document in Mongo (browser/OS/device/geo — schema-less, grows independently of the
// core record). The two writes aren't transactional with each other, so redelivery has to handle
// each independently: the event carries its own Id (generated once by RedirectApi), so a message
// whose Id is already in Postgres skips only the Postgres insert (avoiding the primary-key
// violation) but still goes on to attempt the Mongo write every time - that write is itself
// idempotent (upsert by Id, see MongoClickMetaStore), so retrying it on redelivery is always safe,
// and skipping it just because Postgres already has the row would otherwise mean any redelivery
// caused by a transient Mongo failure permanently loses that click's metadata.
public sealed class ClickTrackedConsumer(
    IMessageConsumer consumer,
    IDbContextFactory<DatabaseContext> dbContextFactory,
    IClickMetaStore clickMetaStore,
    IUserAgentParser userAgentParser,
    IGeoIpResolver geoIpResolver,
    ILogger<ClickTrackedConsumer> logger) : BackgroundService
{
    private const string QueueName = "traffic-service.click-tracked";

    private readonly IMessageConsumer _consumer = consumer;
    private readonly IDbContextFactory<DatabaseContext> _dbContextFactory = dbContextFactory;
    private readonly IClickMetaStore _clickMetaStore = clickMetaStore;
    private readonly IUserAgentParser _userAgentParser = userAgentParser;
    private readonly IGeoIpResolver _geoIpResolver = geoIpResolver;
    private readonly ILogger<ClickTrackedConsumer> _logger = logger;

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        _consumer.ConsumeAsync<ClickTrackedEvent>(QueueName, Topics.ClickTracked, HandleAsync, stoppingToken);

    internal async Task HandleAsync(ClickTrackedEvent @event, CancellationToken cancellationToken)
    {
        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        var alreadyStored = await context.Clicks.AnyAsync(x => x.Id == @event.Id, cancellationToken);
        if (alreadyStored)
        {
            _logger.LogInformation(
                "Click {ClickId} is already persisted in Postgres - skipping the insert, but still " +
                "re-attempting the Mongo ClickMeta write in case an earlier attempt failed", @event.Id);
        }
        else
        {
            context.Clicks.Add(new Click(@event.Id, @event.ClickedAt, @event.InboundLink, @event.OutboundLink, @event.Hash));
            await context.SaveChangesAsync(cancellationToken);
        }

        var parsedUserAgent = _userAgentParser.Parse(@event.UserAgent);
        var geo = await _geoIpResolver.ResolveAsync(@event.IpAddress, cancellationToken);

        await _clickMetaStore.SaveAsync(
            new ClickMeta(
                @event.Id,
                @event.Hash,
                @event.ClickedAt,
                @event.UserAgent,
                @event.Referrer,
                @event.IpAddress,
                parsedUserAgent.Browser,
                parsedUserAgent.Os,
                parsedUserAgent.DeviceType,
                geo.Country,
                geo.City),
            cancellationToken);

        _logger.LogInformation("Persisted click {ClickId} for hash {Hash}", @event.Id, @event.Hash);
    }
}
