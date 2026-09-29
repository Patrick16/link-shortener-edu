using ControlApi.Models;

namespace ControlApi.Services;

public static class TrafficRequestValidator
{
    // Returns the first validation error, or null when the request is acceptable. Every bound here
    // guards the one externally-triggerable input that spins up real k6 virtual users against the
    // local docker-compose stack.
    public static string? Validate(TrafficRequest request, IDockerService docker)
    {
        if (request.Steps.Count == 0)
        {
            return "at least one endpoint step is required";
        }

        var unknownEndpoints = request.Steps.Where(s => !docker.ListKnownEndpoints().Any(ep => ep.Id == s.EndpointId)).Select(s => s.EndpointId).ToList();
        if (unknownEndpoints.Count > 0)
        {
            return $"unknown endpoint(s): {string.Join(", ", unknownEndpoints)}";
        }

        if (request.Steps.Any(s => s.PauseAfterSeconds is < 0 or > 30))
        {
            return "each step's pauseAfterSeconds must be between 0 and 30";
        }

        if (request.DataPool is { } dataPool && ValidateDataPool(dataPool, docker) is { } dataPoolError)
        {
            return dataPoolError;
        }

        return request.Iterations is { } iterations
            ? ValidateIterationRun(request, iterations)
            : ValidateRampOrDurationRun(request);
    }

    private static string? ValidateDataPool(DataPoolRequest dataPool, IDockerService docker)
    {
        if (!docker.ListDataSources().Any(s => s.Id == dataPool.SourceId))
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
    // Stages/DurationSeconds checks in ValidateRampOrDurationRun apply. Upper bound: with no cap, a
    // typo (or "let's see what happens at 10000 VUs") can exhaust the host's CPU/memory and make the
    // whole sandbox unresponsive, with nothing server-side to stop it. 200 matches the bound this
    // endpoint originally shipped with.
    private static string? ValidateIterationRun(TrafficRequest request, int iterations)
    {
        if (request.Vus is < 1 or > 200)
        {
            return "vus must be between 1 and 200 for an iteration-count run";
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
        // way - see ValidateIterationRun for why an unbounded Vus is a real risk, not just a style nit.
        if (request.Vus < 0 || request.Vus > 200 || (request.Stages is not { Count: > 0 } && request.Vus < 1))
        {
            return "vus must be between 1 and 200 (or at least 0 as a ramp's starting point)";
        }

        if (request.Stages is { Count: > 0 } stages)
        {
            if (stages.Any(s => s.DurationSeconds < 1))
            {
                return "each stage's durationSeconds must be at least 1";
            }

            if (stages.Any(s => s.TargetVus is < 0 or > 200))
            {
                return "a stage's targetVus must be between 0 and 200";
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
