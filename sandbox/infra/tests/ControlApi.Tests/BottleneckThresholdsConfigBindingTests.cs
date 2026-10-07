using ControlApi.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ControlApi.Tests;

// BottleneckThresholds is a record with a primary constructor whose parameters all have C#
// defaults - this verifies partial JSON overrides behave the way a reader of appsettings.json
// would expect (an omitted property keeps the record's own default, not CLR's 0), not just that
// Configure<T>/IOptions wiring compiles. A binder that silently zeroed out unspecified numeric
// thresholds instead of leaving them at their defaults would be a severe, silent bug - e.g.
// HighFailedRequestRate=0 would flag every run that has any failures at all.
public class BottleneckThresholdsConfigBindingTests
{
    private static BottleneckThresholds Bind(string json)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
            .Build();

        var services = new ServiceCollection();
        services.Configure<BottleneckThresholds>(configuration.GetSection("BottleneckThresholds"));
        using var provider = services.BuildServiceProvider();

        return provider.GetRequiredService<IOptions<BottleneckThresholds>>().Value;
    }

    [Fact]
    public void NoConfigSection_BindsToRecordDefaults()
    {
        var thresholds = Bind("{}");

        Assert.Equal(BottleneckThresholds.Default, thresholds);
    }

    [Fact]
    public void PartialOverride_UnspecifiedFieldsKeepRecordDefaults_NotZero()
    {
        var thresholds = Bind("""{ "BottleneckThresholds": { "SlowHopP95Ms": 500 } }""");

        Assert.Equal(500, thresholds.SlowHopP95Ms);
        // These three were never mentioned in the JSON - must stay at the record's own defaults,
        // not fall back to double's CLR default (0).
        Assert.Equal(BottleneckThresholds.Default.HighCpuPercent, thresholds.HighCpuPercent);
        Assert.Equal(BottleneckThresholds.Default.HighMemoryPercent, thresholds.HighMemoryPercent);
        Assert.Equal(BottleneckThresholds.Default.HighFailedRequestRate, thresholds.HighFailedRequestRate);
    }

    [Fact]
    public void FullOverride_AllFieldsBindCorrectly()
    {
        var thresholds = Bind("""
            {
              "BottleneckThresholds": {
                "HighCpuPercent": 70,
                "HighMemoryPercent": 80,
                "SlowHopP95Ms": 150,
                "HighFailedRequestRate": 0.1
              }
            }
            """);

        Assert.Equal(
            new BottleneckThresholds { HighCpuPercent = 70, HighMemoryPercent = 80, SlowHopP95Ms = 150, HighFailedRequestRate = 0.1 },
            thresholds);
    }
}
