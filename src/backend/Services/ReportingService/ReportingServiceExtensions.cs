using Common;
using Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace ReportingService;

// ReportingService-only wiring - the parts of Program.cs that no other service shares. Needs its
// own IUserAgentParser/IGeoIpResolver registrations (same concrete types TrafficService already
// uses) because it's an independent second consumer of the raw ClickTrackedEvent, not a reader of
// TrafficService's already-parsed ClickMeta - each read model is built straight from the source
// event, not from another service's derived data.
public static class ReportingServiceExtensions
{
    public static WebApplicationBuilder AddReportIngestion(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IUserAgentParser, UaParserUserAgentParser>();

        // See GeoIpExtensions.AddGeoIpResolution's own comment - shared with TrafficService so
        // this consumer and TrafficService's share one cache instead of each independently hitting
        // ip-api.com's rate-limited endpoint for the same click (found during review - this service
        // originally doubled TrafficService's request rate against that shared budget).
        builder.AddGeoIpResolution();

        // Unlike ShortenerService/TrafficService, nothing needs to resolve ClickTrackedConsumer
        // directly here - this service doesn't host a messaging-mode gRPC endpoint (see
        // Program.cs's own comment), so the plain IHostedService registration is enough.
        builder.Services.AddHostedService<ClickTrackedConsumer>();
        return builder;
    }
}
