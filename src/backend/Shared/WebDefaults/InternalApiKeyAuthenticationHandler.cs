using System.Security.Claims;
using System.Text.Encodings.Web;
using Common;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace WebDefaults;

// A second, independent authentication scheme alongside JwtBearer, not a replacement for it, and
// never exposed to the frontend or end users. Lets a genuinely internal, non-user caller (currently
// control-api's traffic data-pool preload) authenticate with a shared secret instead of a per-user
// token, for the one endpoint that specifically needs unscoped access across every user's data
// rather than one arbitrary user's own - a per-user JWT can't express "give me a broad sample of
// everyone's data" at all. Register alongside JwtBearer and combine both schemes on the action with
// [Authorize(AuthenticationSchemes = $"{JwtBearerDefaults.AuthenticationScheme},{Constants.InternalApiKeyAuthenticationScheme}")]
// so either succeeding is enough, then check User.HasClaim(Constants.InternalClaim, "true") in the
// action to decide whether to skip per-user filtering.
public sealed class InternalApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IConfiguration configuration) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(Constants.InternalApiKeyHeaderName, out var provided))
        {
            // NoResult, not Fail: this scheme simply wasn't attempted on this request, so a combined
            // [Authorize(AuthenticationSchemes = "Bearer,InternalApiKey")] can still succeed via the
            // other scheme instead of always failing just because this header wasn't present.
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var configuredKey = configuration[Constants.InternalApiKeySection];
        if (string.IsNullOrEmpty(configuredKey) || provided != configuredKey)
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid internal API key."));
        }

        var identity = new ClaimsIdentity([new Claim(Constants.InternalClaim, "true")], Scheme.Name);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
