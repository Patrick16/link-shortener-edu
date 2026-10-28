using System.Net;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Infrastructure.Tests;

// Exercises the concurrency limiter GeoIpExtensions.AddGeoIpResolution wires onto
// IpApiGeoIpResolver's HttpClient (resilience backlog item 15) - unlike IpApiGeoIpResolverTests,
// which constructs IpApiGeoIpResolver directly with a hand-rolled HttpClient and never touches
// the resilience pipeline at all, this goes through the real DI registration so the limiter is
// actually in the call path.
public class GeoIpBulkheadTests
{
    // Blocks every call until released, so a test can deterministically pin exactly PermitLimit
    // calls "in flight" before firing one more - a real race (fire 5 concurrent calls and hope
    // only 2 land before the rest arrive) would be flaky by construction.
    private sealed class BlockingHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _gate = new();
        private int _reachedHandler;

        public int ReachedHandlerCount => _reachedHandler;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _reachedHandler);
            await _gate.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"status":"success","country":"US","city":"Springfield"}"""),
            };
        }

        public void Release() => _gate.TrySetResult();
    }

    private static (IpApiGeoIpResolver Resolver, BlockingHandler Handler) BuildResolver(int permitLimit)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["GeoIp:MaxConcurrentRequests"] = permitLimit.ToString();
        // Real Redis is never touched by this test (only IpApiGeoIpResolver is resolved below,
        // not the CachingGeoIpResolver wrapper IGeoIpResolver resolves to) - AddStackExchangeRedisCache
        // connects lazily on first cache use, which never happens here.
        builder.AddGeoIpResolution();

        var handler = new BlockingHandler();
        builder.Services.AddHttpClient<IpApiGeoIpResolver>().ConfigurePrimaryHttpMessageHandler(() => handler);

        var app = builder.Build();
        return (app.Services.GetRequiredService<IpApiGeoIpResolver>(), handler);
    }

    [Fact]
    public async Task ResolveAsync_MoreConcurrentCallsThanPermitLimit_ExcessCallRejectedWithoutReachingHttpClient()
    {
        var (resolver, handler) = BuildResolver(permitLimit: 2);

        // Two calls occupy both permits and block inside the handler - distinct public IPs so
        // neither short-circuits on IpApiGeoIpResolver's own private/loopback check.
        var blocked1 = resolver.ResolveAsync("1.2.3.4");
        var blocked2 = resolver.ResolveAsync("5.6.7.8");

        await WaitUntilAsync(() => handler.ReachedHandlerCount == 2);

        // QueueLimit is 0 (reject immediately, never queue) - a third call while both permits are
        // held must be rejected by the limiter before ever reaching the handler, and
        // IpApiGeoIpResolver's own catch-all must turn that rejection into "no geo data" rather
        // than letting it throw.
        var rejected = await resolver.ResolveAsync("9.9.9.9");

        Assert.Equal(new GeoLocation(null, null), rejected);
        Assert.Equal(2, handler.ReachedHandlerCount);

        handler.Release();
        var result1 = await blocked1;
        var result2 = await blocked2;
        Assert.Equal(new GeoLocation("US", "Springfield"), result1);
        Assert.Equal(new GeoLocation("US", "Springfield"), result2);
    }

    [Fact]
    public async Task ResolveAsync_WithinPermitLimit_AllCallsReachHttpClient()
    {
        var (resolver, handler) = BuildResolver(permitLimit: 5);

        var call = resolver.ResolveAsync("1.2.3.4");
        await WaitUntilAsync(() => handler.ReachedHandlerCount == 1);
        handler.Release();

        var result = await call;

        Assert.Equal(new GeoLocation("US", "Springfield"), result);
        Assert.Equal(1, handler.ReachedHandlerCount);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was never met within 5s");
            }

            await Task.Delay(10);
        }
    }
}
