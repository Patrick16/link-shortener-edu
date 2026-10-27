using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using WebDefaults;

namespace Integration.Tests;

// A one-line Configure<HostOptions> call has nothing else to catch a future accidental removal,
// or a merge of this into AddWebApiDefaults (which AuthApi also calls, and which this value is
// deliberately NOT folded into - see AddGracefulShutdown's own comment) (found during review).
public class GracefulShutdownTests
{
    [Fact]
    public void AddGracefulShutdown_SetsShutdownTimeoutTo15Seconds()
    {
        var builder = WebApplication.CreateSlimBuilder();

        builder.AddGracefulShutdown();

        using var app = builder.Build();
        var hostOptions = app.Services.GetRequiredService<IOptions<HostOptions>>().Value;
        Assert.Equal(TimeSpan.FromSeconds(15), hostOptions.ShutdownTimeout);
    }
}
