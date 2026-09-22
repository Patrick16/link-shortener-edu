using Common;
using Common.Models;
using Contracts.Events;
using Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace TrafficService;

// Consumes ClickTrackedEvent and persists it to Postgres Clicks (the core click record) and a
// ClickMeta document in Mongo (browser/OS/device/geo — schema-less, grows independently of the
// core record). Redelivery-safe on the Postgres side: the event carries its own Id (generated once
// by RedirectApi), so a message whose Id is already stored is treated as already processed and
// skipped entirely, rather than failing on the primary-key violation — Mongo isn't touched again
// either in that case. The Mongo write itself is also independently idempotent (upsert by Id, see
// MongoClickMetaStore), since the two writes aren't transactional with each other.
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
            _logger.LogInformation("Click {ClickId} is already persisted, skipping redelivered message", @event.Id);
            return;
        }

        context.Clicks.Add(new Click(@event.Id, @event.ClickedAt, @event.InboundLink, @event.OutboundLink, @event.Hash));
        await context.SaveChangesAsync(cancellationToken);

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
