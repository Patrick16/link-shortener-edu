namespace ControlApi.Models;

// Configurable via the "BottleneckThresholds" section in appsettings.json (see
// ControlPlaneServiceExtensions.AddControlPlaneServices's Configure<BottleneckThresholds> call) -
// these used to be hardcoded constants in BottleneckAdvisor. Default values are unchanged from
// those constants, so an unconfigured deployment behaves exactly as before. See BottleneckAdvisor's
// own header comment for why these are deliberately generous: this is a teaching tool on a
// laptop-scale stack, not a tuned production alerting system.
//
// Init-only properties, not a primary-constructor record - IOptions<T>'s OptionsFactory creates
// the base instance via Activator.CreateInstance<T>() before the configuration binder touches it,
// which needs an actual parameterless constructor. A record whose only constructor is a primary
// one with default parameter values (`record Foo(double A = 1)`) does NOT get one - `new Foo()` is
// just the compiler filling in defaults at the call site, not a real zero-arg constructor in the
// type's metadata - and throws MissingMethodException at the first IOptions<T>.Value read. Caught
// live via a dedicated binding test (BottleneckThresholdsConfigBindingTests) before this shipped;
// this property-init shape is what the regular ASP.NET Core "Options" pattern uses specifically to
// avoid that trap.
public sealed record BottleneckThresholds
{
    public double HighCpuPercent { get; init; } = 85;
    public double HighMemoryPercent { get; init; } = 90;
    public double SlowHopP95Ms { get; init; } = 300;
    public double HighFailedRequestRate { get; init; } = 0.05;

    // The single source of truth for "what BottleneckAdvisor does when nothing configures it" -
    // BottleneckAdvisor.Analyze's own optional parameter defaults to this, and appsettings.json's
    // BottleneckThresholds section is meant to mirror these same numbers (JSON can't reference a
    // C# default directly, so the two have to be kept in sync by hand if either changes).
    public static readonly BottleneckThresholds Default = new();
}
