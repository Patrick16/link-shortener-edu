using System.Text;
using Common;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace WebDefaults;

public static class JwtAuthenticationExtensions
{
    // Shared by every service that validates AuthApi-issued tokens (LinkApi/RedirectApi optionally,
    // ReportingApi always - see its own Program.cs). Jwt:* config keys must match what AuthApi signs
    // tokens with (see Common.Constants) or every token fails validation here. Returns the
    // AuthenticationBuilder, not the WebApplicationBuilder, so a caller that also needs a second
    // scheme (LinkApi's InternalApiKey - see LinkApiServiceExtensions) can chain straight onto this
    // same AddAuthentication() call instead of starting a second, conflicting one.
    public static AuthenticationBuilder AddJwtBearerAuthentication(this WebApplicationBuilder builder)
    {
        var configuration = builder.Configuration;
        var jwtSigningKey = configuration[Constants.JwtSigningKeySection];

        return builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                // Without this, the handler silently renames the "sub" claim to the legacy
                // ClaimTypes.NameIdentifier URI, and User.FindFirst(JwtRegisteredClaimNames.Sub)
                // would never find it. Keep claim types exactly as the token declares them.
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = configuration[Constants.JwtIssuerSection],
                    ValidateAudience = true,
                    ValidAudience = configuration[Constants.JwtAudienceSection],
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSigningKey ?? string.Empty)),
                };
            });
    }
}
