using System.Text;
using Common;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using WebDefaults;

namespace LinkApi;

// LinkApi-only wiring - the parts of Program.cs that no other service shares.
public static class LinkApiServiceExtensions
{
    public static WebApplicationBuilder AddLinkServices(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IEntityCacheService<Common.Models.Link>, LinkCacheService>();
        builder.Services.AddSingleton<IHashGenerator, Sha256Base62HashGenerator>();
        return builder;
    }

    // CreateLink has no [Authorize] - an anonymous request is never rejected there, this only lets it
    // read the caller's userId from a valid Bearer token when one is present. GetLinks does require
    // [Authorize] (per-user listing); it also accepts the InternalApiKey scheme below so a genuinely
    // internal, non-user caller (control-api's data-pool preload) can get unscoped access instead.
    // Jwt* config keys are shared with AuthApi, which issues the tokens (see Common.Constants) - they
    // have to match or every token would fail validation here.
    public static WebApplicationBuilder AddJwtAndInternalApiKeyAuthentication(this WebApplicationBuilder builder)
    {
        var configuration = builder.Configuration;
        var jwtSigningKey = configuration[Constants.JwtSigningKeySection];

        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                // Without this, the handler silently renames the "sub" claim to the legacy
                // ClaimTypes.NameIdentifier URI, and User.FindFirst(JwtRegisteredClaimNames.Sub) in
                // LinksController would never find it. Keep claim types exactly as the token declares them.
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
            })
            .AddScheme<AuthenticationSchemeOptions, InternalApiKeyAuthenticationHandler>(
                Constants.InternalApiKeyAuthenticationScheme, null);
        return builder;
    }
}
