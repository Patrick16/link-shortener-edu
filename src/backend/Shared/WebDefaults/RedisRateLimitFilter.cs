using Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace WebDefaults;

// Hand-rolled instead of Microsoft.AspNetCore.RateLimiting's own middleware - that library's
// RateLimitPartition.Get(partitionKey, factory) takes a SYNCHRONOUS factory that builds a
// long-lived, in-memory RateLimiter per partition key (see AuthApiServiceExtensions' own prior
// comment on why AddPolicy+RateLimitPartition, not AddFixedWindowLimiter, was already needed just
// to get a separate bucket per client). Neither shape fits "ask Redis fresh on every request,
// atomically, with an explicit fail-open/fail-closed choice when Redis itself can't answer" (see
// RedisRateLimiter's own comment for why the Redis side needs a single Lua script, not two round
// trips) - trying to shoehorn that into a custom RateLimiter subclass would fight the built-in
// middleware's own assumptions more than it would reuse them.
//
// One filter implementation, not one per app - only the partition-key selector, the numbers, and
// the failure mode actually differ between AuthApi and RedirectApi (see each app's own
// AddXRateLimiting extension for its RateLimitPolicy); the Redis-check-and-reject mechanics below
// are identical, so two copies would just be the same bug waiting to diverge twice.
public sealed class RedisRateLimitFilter(
    IRedisRateLimiter rateLimiter,
    ILocalFallbackRateLimiter localFallbackLimiter,
    RateLimitPolicy policy) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var partitionKey = policy.PartitionKeySelector(context.HttpContext);
        var outcome = await rateLimiter.CheckAsync(
            $"ratelimit:{policy.KeyPrefix}:{partitionKey}",
            policy.PermitLimit,
            policy.Window,
            context.HttpContext.RequestAborted);

        // LocalFallback only ever applies on THIS outcome - Allowed/Denied already came from a real,
        // Redis-backed check and are trusted as-is. Re-expressed as Allowed/Denied (not a third
        // branch below) so the existing rejection logic/messaging doesn't need its own LocalFallback
        // case - from here on, a LocalFallback policy behaves exactly like FailOpen/FailClosed would
        // on whichever outcome the in-memory limiter just decided.
        if (outcome == RateLimitOutcome.RedisUnavailable && policy.FailureMode == RateLimitFailureMode.LocalFallback)
        {
            outcome = localFallbackLimiter.TryAcquire(policy.KeyPrefix, partitionKey, policy.PermitLimit, policy.Window)
                ? RateLimitOutcome.Allowed
                : RateLimitOutcome.Denied;
        }

        var rejected = outcome switch
        {
            RateLimitOutcome.Denied => true,
            // Allowed never rejects; RedisUnavailable rejects only under FailClosed - see
            // RateLimitFailureMode's own comment for why this differs per app. LocalFallback never
            // reaches this branch: the check above already resolved it to Allowed/Denied.
            RateLimitOutcome.RedisUnavailable => policy.FailureMode == RateLimitFailureMode.FailClosed,
            _ => false,
        };

        if (!rejected)
        {
            await next();
            return;
        }

        await WriteRejectionAsync(context.HttpContext, outcome);
        // EmptyResult, not leaving Result unset - the response body/status are already written
        // directly via IProblemDetailsService below (same pattern AuthApi's prior OnRejected
        // handler used), this just tells MVC's own filter pipeline the short-circuit is
        // deliberate, not a bug.
        context.Result = new EmptyResult();
    }

    private static async Task WriteRejectionAsync(HttpContext httpContext, RateLimitOutcome outcome)
    {
        // Same {Title, Detail, Type} shape as every other error response in this API - found
        // during AuthApi's original rate-limiter work that a 429 otherwise comes back as bare
        // {status, traceId} JSON (ASP.NET Core's own default once AddProblemDetails() is
        // registered), unlike a controller's own Problem(...) calls.
        var problemDetails = outcome == RateLimitOutcome.Denied
            ? new ProblemDetails
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = "Too many requests.",
                Detail = "Too many requests from this client in a short time - wait and try again.",
                Type = "https://tools.ietf.org/html/rfc6585#section-4",
            }
            : new ProblemDetails
            {
                // Only reachable under FailClosed (see OnActionExecutionAsync) - Redis itself is
                // unreachable and this endpoint deliberately refuses to let every request through
                // unchecked rather than risk it (e.g. AuthApi's login, where an open window during
                // an outage means unlimited brute-force attempts).
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "Rate limiter unavailable.",
                Detail = "The rate limiter's backing store is unreachable and this endpoint fails closed rather than letting every request through unchecked - try again shortly.",
            };

        httpContext.Response.StatusCode = problemDetails.Status!.Value;
        var problemDetailsService = httpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
        await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problemDetails,
        });
    }
}
