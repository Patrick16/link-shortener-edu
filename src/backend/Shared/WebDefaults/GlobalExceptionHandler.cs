using Grpc.Core;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

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

        logger.LogError(exception, "Unhandled exception processing {Method} {Path}",
            httpContext.Request.Method, httpContext.Request.Path);

        // A SyncGrpcDispatcher call (messaging-mode=grpc) failing is the one exception type this
        // handler treats specially - it's not "this service is broken", it's "the downstream
        // service this request synchronously depends on is unreachable", which 502/503 says more
        // honestly than a generic 500. Deliberately NOT retried or queued here - see
        // SyncGrpcDispatcher's own comment for why swallowing this would hide the exact coupling
        // cost this messaging mode exists to demonstrate. InvalidArgument is excluded here (falls
        // through to the generic 500 below) - that status means the gRPC client/server have
        // drifted on the topic/payload contract (see each worker's MessagingGrpcService), a real
        // bug, not "downstream unavailable" (found during review - it was previously
        // indistinguishable from an actual outage).
        if (exception is RpcException { StatusCode: not StatusCode.InvalidArgument } rpcException)
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
