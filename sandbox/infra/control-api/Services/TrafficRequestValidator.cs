using ControlApi.Models;

namespace ControlApi.Services;

public static class TrafficRequestValidator
{
    // Returns the first validation error, or null when the request is acceptable. Every bound here
    // guards the one externally-triggerable input that spins up real k6 virtual users against the
    // local docker-compose stack.
    public static string? Validate(TrafficRequest request, ITrafficService traffic)
    {
        if (request.Steps.Count == 0)
        {
            return "at least one endpoint step is required";
        }

        var unknownEndpoints = request.Steps.Where(s => !traffic.ListKnownEndpoints().Any(ep => ep.Id == s.EndpointId)).Select(s => s.EndpointId).ToList();
        if (unknownEndpoints.Count > 0)
        {
            return $"unknown endpoint(s): {string.Join(", ", unknownEndpoints)}";
        }

        if (request.Steps.Any(s => s.PauseAfterSeconds is < 0 or > 30))
        {
            return "each step's pauseAfterSeconds must be between 0 and 30";
        }

        if (request.DataPool is { } dataPool && ValidateDataPool(dataPool, traffic) is { } dataPoolError)
        {
            return dataPoolError;
        }

        return request.Iterations is { } iterations
            ? ValidateIterationRun(request, iterations)
            : ValidateRampOrDurationRun(request);
    }

    private static string? ValidateDataPool(DataPoolRequest dataPool, ITrafficService traffic)
    {
        if (!traffic.ListDataSources().Any(s => s.Id == dataPool.SourceId))
        {
            return $"unknown data source: {dataPool.SourceId}";
        }

        if (dataPool.Count is < 1 or > 20_000)
        {
            return "dataPool.count must be between 1 and 20000";
        }

        if (dataPool.Mode is not ("sequential" or "random"))
        {
            return "dataPool.mode must be 'sequential' or 'random'";
        }

        return null;
    }

    // Iteration-count runs use k6's shared-iterations executor - flat VUs, no ramp, so none of the
    // Stages/DurationSeconds checks in ValidateRampOrDurationRun apply. Vus has no upper bound (the
    // original 200 cap was lifted on purpose for high-load experiments) - a typo can exhaust the
    // host's CPU/memory, so be deliberate with the number.
    private static string? ValidateIterationRun(TrafficRequest request, int iterations)
    {
        if (request.Vus < 1)
        {
            return "vus must be at least 1 for an iteration-count run";
        }

        if (iterations is < 1 or > 100_000)
        {
            return "iterations must be between 1 and 100000";
        }

        return null;
    }

    private static string? ValidateRampOrDurationRun(TrafficRequest request)
    {
        // With a custom ramp, Vus is just the starting point k6 ramps from - 0 is exactly what a spike
        // profile (or any "ramp up from idle") wants there. Only the flat constant-VUs run needs it to
        // be at least 1, since there it's the VU count for the entire run. Upper bound applies either
        // way - see ValidateIterationRun: there is deliberately no upper bound on Vus.
        if (request.Vus < 0 || (request.Stages is not { Count: > 0 } && request.Vus < 1))
        {
            return "vus must be at least 1 (or at least 0 as a ramp's starting point)";
        }

        if (request.Stages is { Count: > 0 } stages)
        {
            if (stages.Any(s => s.DurationSeconds < 1))
            {
                return "each stage's durationSeconds must be at least 1";
            }

            if (stages.Any(s => s.TargetVus < 0))
            {
                return "a stage's targetVus must be at least 0";
            }

            // A custom ramp is user-drawn, so it isn't bound by the flat run's 120s cap - just a
            // generous ceiling so a slipped point on the graph can't lock up a k6 container forever.
            if (stages.Sum(s => s.DurationSeconds) > 600)
            {
                return "total stage duration can't exceed 600 seconds";
            }
        }
        else if (request.DurationSeconds is < 1 or > 120)
        {
            return "durationSeconds must be between 1 and 120";
        }

        return null;
    }
}
