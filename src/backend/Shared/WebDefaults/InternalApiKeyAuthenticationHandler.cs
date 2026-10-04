using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
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
        if (string.IsNullOrEmpty(configuredKey) || !IsMatch(provided.ToString(), configuredKey))
        {
            // Warning, not Debug - this scheme is only ever meant to be used by control-api, so a
            // rejected attempt is either a misconfiguration or something worth a second look, not
            // routine traffic. Never logs the provided key itself.
            Logger.LogWarning("Rejected internal API key authentication attempt");
            return Task.FromResult(AuthenticateResult.Fail("Invalid internal API key."));
        }

        var identity = new ClaimsIdentity([new Claim(Constants.InternalClaim, "true")], Scheme.Name);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    // Ordinary string equality short-circuits on the first differing character - this scheme is
    // reachable from LinkApi's normal public surface (nginx proxies straight through, see
    // sandbox/infra/nginx/nginx.conf), so that timing difference is a real, remotely observable side
    // channel an attacker could use to recover the key byte-by-byte across repeated guesses.
    // FixedTimeEquals compares in constant time; only a length mismatch (not sensitive here, unlike
    // content) is allowed to short-circuit first.
    private static bool IsMatch(string provided, string configured)
    {
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        var configuredBytes = Encoding.UTF8.GetBytes(configured);
        return providedBytes.Length == configuredBytes.Length &&
            CryptographicOperations.FixedTimeEquals(providedBytes, configuredBytes);
    }
}
