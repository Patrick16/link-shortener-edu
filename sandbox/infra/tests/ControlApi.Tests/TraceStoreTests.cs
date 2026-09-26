using ControlApi.Models;
using ControlApi.Services;

namespace ControlApi.Tests;

public class TraceStoreTests
{
    private static OtlpExportTraceServiceRequest Request(string serviceName, params (string Name, long StartNano, long EndNano)[] spans) =>
        new([
            new OtlpResourceSpans(
                new OtlpResource([new OtlpKeyValue("service.name", new OtlpAnyValue(serviceName))]),
                [
                    new OtlpScopeSpans(spans.Select(s => new OtlpSpan(s.Name, Kind: 1, s.StartNano.ToString(), s.EndNano.ToString())).ToList()),
                ]),
        ]);

    private static long UnixNano(DateTimeOffset time) => time.ToUnixTimeMilliseconds() * 1_000_000;

    [Fact]
    public void Ingest_KnownServiceName_MapsToServiceId()
    {
        var sut = new TraceStore();
        var start = DateTimeOffset.UtcNow;
        sut.Ingest(Request("LinkApi", ("POST /Links", UnixNano(start), UnixNano(start.AddMilliseconds(50)))));

        var spans = sut.GetSpansBetween(start.AddSeconds(-1), start.AddSeconds(1));

        var span = Assert.Single(spans);
        Assert.Equal("link-api", span.ServiceId);
        Assert.Equal("POST /Links", span.SpanName);
        Assert.Equal(50, span.DurationMs, precision: 0);
    }

    [Fact]
    public void Ingest_UnknownServiceName_IsIgnored()
    {
        var sut = new TraceStore();
        var start = DateTimeOffset.UtcNow;
        sut.Ingest(Request("SomeOtherProcess", ("GET /", UnixNano(start), UnixNano(start.AddMilliseconds(10)))));

        var spans = sut.GetSpansBetween(start.AddSeconds(-1), start.AddSeconds(1));

        Assert.Empty(spans);
    }

    [Fact]
    public void Ingest_MissingServiceNameAttribute_IsIgnored()
    {
        var sut = new TraceStore();
        var request = new OtlpExportTraceServiceRequest([
            new OtlpResourceSpans(
                new OtlpResource([]),
                [new OtlpScopeSpans([new OtlpSpan("GET /", 1, "1000000000", "1000500000")])]),
        ]);

        sut.Ingest(request);

        Assert.Empty(sut.GetSpansBetween(DateTimeOffset.MinValue, DateTimeOffset.MaxValue));
    }

    [Fact]
    public void Ingest_UnparseableTimestamps_SkipsSpanWithoutThrowing()
    {
        var sut = new TraceStore();
        var request = new OtlpExportTraceServiceRequest([
            new OtlpResourceSpans(
                new OtlpResource([new OtlpKeyValue("service.name", new OtlpAnyValue("LinkApi"))]),
                [new OtlpScopeSpans([new OtlpSpan("POST /Links", 1, StartTimeUnixNano: "not-a-number", EndTimeUnixNano: "1000500000")])]),
        ]);

        sut.Ingest(request);

        Assert.Empty(sut.GetSpansBetween(DateTimeOffset.MinValue, DateTimeOffset.MaxValue));
    }

    [Fact]
    public void GetSpansBetween_ExcludesSpansOutsideWindow()
    {
        var sut = new TraceStore();
        var now = DateTimeOffset.UtcNow;
        sut.Ingest(Request("LinkApi",
            ("inside", UnixNano(now), UnixNano(now.AddMilliseconds(10))),
            ("before", UnixNano(now.AddMinutes(-5)), UnixNano(now.AddMinutes(-5).AddMilliseconds(10))),
            ("after", UnixNano(now.AddMinutes(5)), UnixNano(now.AddMinutes(5).AddMilliseconds(10)))));

        var spans = sut.GetSpansBetween(now.AddSeconds(-1), now.AddSeconds(1));

        var span = Assert.Single(spans);
        Assert.Equal("inside", span.SpanName);
    }

    [Fact]
    public void GetHopStatsBetween_GroupsByServiceAndSpanName_ComputesAggregates()
    {
        var sut = new TraceStore();
        var start = DateTimeOffset.UtcNow;
        sut.Ingest(Request("LinkApi",
            ("POST /Links", UnixNano(start), UnixNano(start.AddMilliseconds(10))),
            ("POST /Links", UnixNano(start), UnixNano(start.AddMilliseconds(20))),
            ("POST /Links", UnixNano(start), UnixNano(start.AddMilliseconds(30))),
            ("GET /health/live", UnixNano(start), UnixNano(start.AddMilliseconds(1)))));

        var hops = sut.GetHopStatsBetween(start.AddSeconds(-1), start.AddSeconds(1));

        var createHop = Assert.Single(hops, h => h.SpanName == "POST /Links");
        Assert.Equal("link-api", createHop.ServiceId);
        Assert.Equal(3, createHop.Count);
        Assert.Equal(20, createHop.AvgMs, precision: 0);
        Assert.Equal(30, createHop.MaxMs, precision: 0);

        var healthHop = Assert.Single(hops, h => h.SpanName == "GET /health/live");
        Assert.Equal(1, healthHop.Count);
    }

    [Fact]
    public void GetHopStatsBetween_NoSpansInWindow_ReturnsEmpty()
    {
        var sut = new TraceStore();

        var hops = sut.GetHopStatsBetween(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow);

        Assert.Empty(hops);
    }
}
