using Grpc.Core;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;

namespace WebDefaults;

// Last-resort catch for anything a controller lets escape, so a caller always gets the same
// ProblemDetails (RFC 7807) shape back — never a bare 500 or, in Development, the HTML diagnostic
// page. Controller code that already returns Problem(...)/NotFound()/etc. never reaches this;
// it only runs for genuinely unhandled exceptions.
internal sealed class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        // A client disconnecting (browser tab closed, fetch aborted) while a SyncGrpcDispatcher
        // call is in flight surfaces here as RpcException(Cancelled), tied to the very
        // cancellationToken that fired - not a downstream failure, and not worth logging as an
        // unhandled error or writing a response for (there's no client left to receive it). Found
        // during review: without this check, every benign cancellation in gRPC mode was logged as
        // an error and reported as a fabricated "downstream unavailable" outage.
        if (exception is RpcException { StatusCode: StatusCode.Cancelled } && cancellationToken.IsCancellationRequested)
        {
            return true;
        }

        // AlreadyExists is a real, expected business outcome (today: a custom alias losing a
        // uniqueness race in ShortenerService's synchronous create path - see
        // CustomAliasCreator/LinkCreatedConsumer), not a transport failure or a bug - 409 says that
        // honestly, with the same reason text that was also dead-lettered server-side. Handled
        // (and, like the Cancelled case above, deliberately NOT routed through the LogError below)
        // before that unconditional call, not after - losing a routine alias race is ordinary user
        // behavior, not an unhandled-exception-worthy event; logging it as Error would mean every
        // "that name's taken" response shows up as an error-rate spike in Grafana/Loki (found during
        // review - the original version logged this at Error alongside genuine outages/bugs).
        if (exception is RpcException { StatusCode: StatusCode.AlreadyExists } conflictException)
        {
            logger.LogInformation("Alias/hash conflict on {Method} {Path}: {Reason}",
                httpContext.Request.Method, httpContext.Request.Path, conflictException.Status.Detail);

            httpContext.Response.StatusCode = StatusCodes.Status409Conflict;

            return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = httpContext,
                Exception = exception,
                ProblemDetails = new ProblemDetails
                {
                    Status = StatusCodes.Status409Conflict,
                    Title = "Conflict.",
                    Detail = conflictException.Status.Detail,
                    Type = "https://tools.ietf.org/html/rfc7231#section-6.5.8",
                },
            });
        }

        logger.LogError(exception, "Unhandled exception processing {Method} {Path}",
            httpContext.Request.Method, httpContext.Request.Path);

        // A SyncGrpcDispatcher call (messaging-mode=grpc) failing is the one exception type this
        // handler treats specially - it's not "this service is broken", it's "the downstream
        // service this request synchronously depends on is unreachable", which 502/503 says more
        // honestly than a generic 500. Deliberately NOT retried or queued here - see
        // SyncGrpcDispatcher's own comment for why swallowing this would hide the exact coupling
        // cost this messaging mode exists to demonstrate. InvalidArgument and AlreadyExists are
        // excluded here (InvalidArgument falls through to the generic 500 below, AlreadyExists is
        // handled above) - neither means "downstream unavailable": InvalidArgument means the gRPC
        // client/server have drifted on the topic/payload contract (see each worker's
        // MessagingGrpcService), a real bug (found during review - it was previously
        // indistinguishable from an actual outage); AlreadyExists is a real business conflict.
        if (exception is RpcException { StatusCode: not (StatusCode.InvalidArgument or StatusCode.AlreadyExists) } rpcException)
        {
            var grpcStatusCode = rpcException.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded
                ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status502BadGateway;

            httpContext.Response.StatusCode = grpcStatusCode;

            return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = httpContext,
                Exception = exception,
                ProblemDetails = new ProblemDetails
                {
                    Status = grpcStatusCode,
                    Title = "Downstream service unavailable.",
                    Detail = "The synchronous gRPC call to the downstream service failed. No fallback queue exists for this messaging mode.",
                    Type = "https://tools.ietf.org/html/rfc7231#section-6.6.4",
                },
            });
        }

        // A Polly circuit breaker (Redis/gRPC/RabbitMQ, see ResiliencePipelines) that's currently
        // open throws this instead of letting the call attempt and time out. Same honest shape as
        // the RpcException branch above - "a dependency this request needs is currently being
        // bypassed," not "this service itself is broken" - and the same status code a slow timeout
        // from the same dependency would eventually have produced anyway, just without the wait.
        if (exception is BrokenCircuitException)
        {
            httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;

            return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = httpContext,
                Exception = exception,
                ProblemDetails = new ProblemDetails
                {
                    Status = StatusCodes.Status503ServiceUnavailable,
                    Title = "Downstream service unavailable.",
                    Detail = "A circuit breaker is currently open for this dependency - failing fast instead of waiting out its timeout.",
                    Type = "https://tools.ietf.org/html/rfc7231#section-6.6.4",
                },
            });
        }

        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;

        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "An unexpected error occurred.",
                Type = "https://tools.ietf.org/html/rfc7231#section-6.6.1",
            },
        });
    }
}
