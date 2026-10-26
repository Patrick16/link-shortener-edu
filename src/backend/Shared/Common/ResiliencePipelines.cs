using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;

namespace Common;

// Shared circuit-breaker tuning for every dependency this project wraps with Polly directly
// (Redis, the gRPC messaging-mode dispatcher, the RabbitMQ publisher - see Constants for the
// keyed-DI key each one registers under). ip-api.com's HTTP client uses
// Microsoft.Extensions.Http.Resilience's own AddResilienceHandler instead (see GeoIpExtensions),
// since HttpClient already has an idiomatic resilience-pipeline hook built in - this builder is
// only for the three dependencies with no such hook, where the call site has to wrap itself
// explicitly with ExecuteAsync.
//
// One shared builder, not one bespoke configuration per call site, so three independent wrappers
// can't silently drift onto three different breaker tunings - and one place to explain the two
// non-obvious choices below instead of three copies of the same comment.
public static class ResiliencePipelines
{
    public static ResiliencePipeline BuildCircuitBreaker(string dependencyName, ILogger logger)
    {
        return new ResiliencePipelineBuilder()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                // A caller cancelling (client disconnect, an upstream deadline) is not the
                // dependency's fault - counting it as a failure would let an unrelated flood of
                // cancellations trip the breaker open for every other caller too.
                ShouldHandle = new PredicateBuilder().Handle<Exception>(ex => ex is not OperationCanceledException),
                FailureRatio = 0.5,
                MinimumThroughput = 10,
                SamplingDuration = TimeSpan.FromSeconds(30),
                // Long enough that a brief blip doesn't thrash the breaker open/closed on every
                // sampling window; short enough that a recovered dependency isn't left bypassed
                // for minutes after it's actually back.
                BreakDuration = TimeSpan.FromSeconds(15),
                OnOpened = args =>
                {
                    logger.LogWarning(args.Outcome.Exception,
                        "Circuit breaker for {Dependency} opened - failing fast for {BreakDuration} instead of paying this dependency's real timeout on every call",
                        dependencyName, args.BreakDuration);
                    ResilienceMetrics.CircuitBreakerStateTransitions.Add(1,
                        new KeyValuePair<string, object?>("dependency", dependencyName),
                        new KeyValuePair<string, object?>("state", "opened"));
                    return default;
                },
                OnClosed = _ =>
                {
                    logger.LogInformation("Circuit breaker for {Dependency} closed - calls flowing normally again", dependencyName);
                    ResilienceMetrics.CircuitBreakerStateTransitions.Add(1,
                        new KeyValuePair<string, object?>("dependency", dependencyName),
                        new KeyValuePair<string, object?>("state", "closed"));
                    return default;
                },
                // Polly's own state machine (not reimplemented here) only lets exactly one probe
                // call through while half-open - every other concurrent caller gets a
                // BrokenCircuitException immediately until that probe resolves, so a flood of
                // requests arriving the moment the break duration elapses can't re-kill a
                // still-recovering dependency with a parallel retry storm.
                OnHalfOpened = _ =>
                {
                    logger.LogInformation("Circuit breaker for {Dependency} half-open - letting one probe call through", dependencyName);
                    ResilienceMetrics.CircuitBreakerStateTransitions.Add(1,
                        new KeyValuePair<string, object?>("dependency", dependencyName),
                        new KeyValuePair<string, object?>("state", "half-opened"));
                    return default;
                },
            })
            .Build();
    }
}
