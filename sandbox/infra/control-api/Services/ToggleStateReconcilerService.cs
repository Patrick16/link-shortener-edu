namespace ControlApi.Services;

// Runs exactly once, at control-api startup - see InfraToggleService's own comment on its cached
// toggle fields for why this exists (a restart resets them to hardcoded defaults, desynced from
// whatever's actually running, and the NEXT unrelated toggle flip would recreate services against
// that stale state). BackgroundService rather than a plain startup call in Program.cs: this needs
// IInfraToggleService resolved from DI after the host's service provider is built, and runs best
// as a fire-and-forget task so a slow/unreachable Docker socket at boot doesn't block control-api
// itself from starting - every other endpoint still works regardless of whether this finishes.
public class ToggleStateReconcilerService(
    IInfraToggleService toggles,
    ILogger<ToggleStateReconcilerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await toggles.ReconcileFromLiveContainersAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort, same reasoning as PgcatService.ReloadAllInstancesAsync: a failure here
            // (Docker socket unreachable at boot) just means this run keeps whichever hardcoded
            // defaults InfraToggleService started with - no worse than before this service existed.
            logger.LogWarning(ex, "Startup toggle-state reconciliation against live containers failed - keeping hardcoded defaults");
        }
    }
}
