using ControlApi.Models;

namespace ControlApi.Services;

public interface IChaosService
{
    // Runs Pumba against the target's network - the target container itself keeps running
    // throughout, only its network behaves worse. Self-heals after DurationSeconds; returns null
    // if the target service isn't found.
    Task<ChaosAction?> DegradeAsync(string serviceId, ChaosRequest request, CancellationToken ct);

    // Stops any in-progress chaos against a service early instead of waiting out its duration.
    // Returns how many chaos containers were stopped.
    Task<int> HealAsync(string serviceId, CancellationToken ct);
}
