using System.Net;
using Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using WebDefaults;

namespace Integration.Tests;

// RedisRateLimitFilter is an IAsyncActionFilter, which only ever runs inside MVC's own action
// pipeline - unlike WebDefaults' other components (e.g. GlobalExceptionHandler, tested via a full
// TestServer in ApiExceptionHandlingTests), there's no lighter-weight way to exercise it than
// constructing the same filter-context types MVC itself builds. A hand-rolled fake
// IRedisRateLimiter (not Moq, which this project doesn't reference) stands in for the real Redis
// check, so these tests control Allowed/Denied/RedisUnavailable directly instead of needing a
// real Redis.
public class RedisRateLimitFilterTests
{
    private sealed class FakeRateLimiter(RateLimitOutcome outcome) : IRedisRateLimiter
    {
        public string? LastKey { get; private set; }

        public Task<RateLimitOutcome> CheckAsync(string key, int permitLimit, TimeSpan window, CancellationToken cancellationToken)
        {
            LastKey = key;
            return Task.FromResult(outcome);
        }
    }

    private static ActionExecutingContext NewExecutingContext(string remoteIp = "1.2.3.4")
    {
        var services = new ServiceCollection();
        // AddProblemDetails() alone isn't enough on a bare ServiceCollection (unlike a full
        // WebApplicationBuilder's, which already has this) - DefaultProblemDetailsWriter needs
        // IOptions<ProblemDetailsOptions>, which needs the base AddOptions() registrations.
        services.AddOptions();
        services.AddProblemDetails();
        var httpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        httpContext.Response.Body = new MemoryStream();
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        return new ActionExecutingContext(
            actionContext, [], new Dictionary<string, object?>(), controller: new object());
    }

    private static ActionExecutionDelegate NextDelegate(ActionContext actionContext, Action onCalled) => () =>
    {
        onCalled();
        return Task.FromResult(new ActionExecutedContext(actionContext, [], controller: new object()));
    };

    private static RateLimitPolicy NewPolicy(RateLimitFailureMode failureMode) => new(
        KeyPrefix: "test",
        PartitionKeySelector: httpContext => httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        PermitLimit: 10,
        Window: TimeSpan.FromSeconds(60),
        FailureMode: failureMode);

    [Fact]
    public async Task OnActionExecutionAsync_Allowed_CallsNext()
    {
        var rateLimiter = new FakeRateLimiter(RateLimitOutcome.Allowed);
        var sut = new RedisRateLimitFilter(rateLimiter, NewPolicy(RateLimitFailureMode.FailOpen));
        var context = NewExecutingContext();
        var nextCalled = false;

        await sut.OnActionExecutionAsync(context, NextDelegate(context, () => nextCalled = true));

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task OnActionExecutionAsync_Denied_Returns429WithoutCallingNext()
    {
        var rateLimiter = new FakeRateLimiter(RateLimitOutcome.Denied);
        var sut = new RedisRateLimitFilter(rateLimiter, NewPolicy(RateLimitFailureMode.FailOpen));
        var context = NewExecutingContext();
        var nextCalled = false;

        await sut.OnActionExecutionAsync(context, NextDelegate(context, () => nextCalled = true));

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status429TooManyRequests, context.HttpContext.Response.StatusCode);
        Assert.IsType<EmptyResult>(context.Result);
    }

    [Fact]
    public async Task OnActionExecutionAsync_RedisUnavailableAndFailOpen_CallsNextAnyway()
    {
        // The whole point of FailOpen - a Redis outage must not block this endpoint.
        var rateLimiter = new FakeRateLimiter(RateLimitOutcome.RedisUnavailable);
        var sut = new RedisRateLimitFilter(rateLimiter, NewPolicy(RateLimitFailureMode.FailOpen));
        var context = NewExecutingContext();
        var nextCalled = false;

        await sut.OnActionExecutionAsync(context, NextDelegate(context, () => nextCalled = true));

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task OnActionExecutionAsync_RedisUnavailableAndFailClosed_Returns503WithoutCallingNext()
    {
        // The whole point of FailClosed - a Redis outage must not open an unlimited window on an
        // endpoint this policy is specifically protecting (e.g. AuthApi's login).
        var rateLimiter = new FakeRateLimiter(RateLimitOutcome.RedisUnavailable);
        var sut = new RedisRateLimitFilter(rateLimiter, NewPolicy(RateLimitFailureMode.FailClosed));
        var context = NewExecutingContext();
        var nextCalled = false;

        await sut.OnActionExecutionAsync(context, NextDelegate(context, () => nextCalled = true));

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.HttpContext.Response.StatusCode);
    }

    [Fact]
    public async Task OnActionExecutionAsync_ComposesRedisKeyFromPolicyPrefixAndPartitionSelector()
    {
        var rateLimiter = new FakeRateLimiter(RateLimitOutcome.Allowed);
        var sut = new RedisRateLimitFilter(rateLimiter, NewPolicy(RateLimitFailureMode.FailOpen));
        var context = NewExecutingContext(remoteIp: "9.8.7.6");

        await sut.OnActionExecutionAsync(context, NextDelegate(context, () => { }));

        Assert.Equal("ratelimit:test:9.8.7.6", rateLimiter.LastKey);
    }
}
