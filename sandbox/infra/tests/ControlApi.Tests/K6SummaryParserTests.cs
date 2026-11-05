using ControlApi.Services.K6;

namespace ControlApi.Tests;

// Same point as K6ProgressParserTests - Parse was already static/pure, but only ever exercised
// through the full Docker-dependent RunTrafficAsync path before this split.
public class K6SummaryParserTests
{
    private const string SampleSummaryJson = """
        {
          "metrics": {
            "http_reqs": { "count": 150, "rate": 15.0 },
            "http_req_failed": { "passes": 3, "value": 0.02 },
            "iterations": { "count": 50, "rate": 5.0 },
            "vus_max": { "value": 10 },
            "http_req_duration": { "avg": 12.5, "min": 1.0, "med": 10.0, "max": 55.0, "p(90)": 20.0, "p(95)": 30.0 },
            "http_reqs{step:link-api.create,status:200}": { "count": 100 },
            "http_reqs{step:link-api.create,status:404}": { "count": 5 },
            "http_reqs{step:redirect-api.resolve,status:302}": { "count": 40 }
          },
          "root_group": {
            "checks": {
              "status is 200": { "passes": 100, "fails": 5 }
            }
          }
        }
        """;

    [Fact]
    public void Parse_FullSummary_MapsEveryTopLevelMetric()
    {
        var report = K6SummaryParser.Parse(SampleSummaryJson, "demo", ["link-api.create", "redirect-api.resolve"], exitCode: 0, rawOutput: "raw");

        Assert.Equal("demo", report.Scenario);
        Assert.Equal(0, report.ExitCode);
        Assert.Equal(150, report.HttpRequests);
        Assert.Equal(15.0, report.HttpRequestRate);
        Assert.Equal(3, report.FailedRequests);
        Assert.Equal(0.02, report.FailedRequestRate);
        Assert.Equal(50, report.Iterations);
        Assert.Equal(5.0, report.IterationRate);
        Assert.Equal(10, report.Vus);
        Assert.Equal("raw", report.RawOutput);
    }

    [Fact]
    public void Parse_HttpReqDurationPresent_MapsLatencyStats()
    {
        var report = K6SummaryParser.Parse(SampleSummaryJson, "demo", [], exitCode: 0, rawOutput: "");

        Assert.NotNull(report.HttpReqDuration);
        Assert.Equal(12.5, report.HttpReqDuration!.Avg);
        Assert.Equal(1.0, report.HttpReqDuration.Min);
        Assert.Equal(10.0, report.HttpReqDuration.Med);
        Assert.Equal(55.0, report.HttpReqDuration.Max);
        Assert.Equal(20.0, report.HttpReqDuration.P90);
        Assert.Equal(30.0, report.HttpReqDuration.P95);
    }

    [Fact]
    public void Parse_NoHttpReqDurationMetric_LeavesItNull()
    {
        var report = K6SummaryParser.Parse("""{"metrics":{}}""", "demo", [], exitCode: 0, rawOutput: "");

        Assert.Null(report.HttpReqDuration);
    }

    [Fact]
    public void Parse_RootGroupChecks_MapsPassesAndFails()
    {
        var report = K6SummaryParser.Parse(SampleSummaryJson, "demo", [], exitCode: 0, rawOutput: "");

        var check = Assert.Single(report.Checks);
        Assert.Equal("status is 200", check.Name);
        Assert.Equal(100, check.Passes);
        Assert.Equal(5, check.Fails);
    }

    [Fact]
    public void Parse_StatusByEndpoint_OnlyIncludesTrackedCodesThatActuallyOccurred()
    {
        var report = K6SummaryParser.Parse(SampleSummaryJson, "demo", ["link-api.create", "redirect-api.resolve"], exitCode: 0, rawOutput: "");

        var createBreakdown = report.StatusBreakdownByEndpoint.Single(e => e.EndpointId == "link-api.create");
        Assert.Equal(2, createBreakdown.StatusCounts.Count);
        Assert.Contains(createBreakdown.StatusCounts, sc => sc.Label == "200 OK" && sc.Count == 100);
        Assert.Contains(createBreakdown.StatusCounts, sc => sc.Label == "404 Not Found" && sc.Count == 5);

        var resolveBreakdown = report.StatusBreakdownByEndpoint.Single(e => e.EndpointId == "redirect-api.resolve");
        var only = Assert.Single(resolveBreakdown.StatusCounts);
        Assert.Equal("302 Found", only.Label);
        Assert.Equal(40, only.Count);
    }

    [Fact]
    public void Parse_StepWithNoTrackedStatusOccurrences_IsOmittedFromTheBreakdown()
    {
        var report = K6SummaryParser.Parse(SampleSummaryJson, "demo", ["link-api.create", "auth-api.login"], exitCode: 0, rawOutput: "");

        Assert.DoesNotContain(report.StatusBreakdownByEndpoint, e => e.EndpointId == "auth-api.login");
    }

    [Fact]
    public void Parse_HttpReqFailed_UsesPassesAsTheFailedCountNotValue()
    {
        // http_req_failed is a Rate metric - "passes" counts samples where the request was
        // considered failed (value 1), "value" is the rate itself. Easy to swap by mistake since
        // "passes" sounds like success for other Rate metrics (e.g. checks).
        var report = K6SummaryParser.Parse(SampleSummaryJson, "demo", [], exitCode: 0, rawOutput: "");

        Assert.Equal(3, report.FailedRequests);
        Assert.NotEqual(0.02, report.FailedRequests);
    }
}
