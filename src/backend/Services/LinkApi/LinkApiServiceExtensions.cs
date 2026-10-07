using Common;
using Microsoft.AspNetCore.Authentication;
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
    // JWT Bearer setup itself lives in WebDefaults (shared with ReportingApi, which validates the
    // same AuthApi-issued tokens but needs no second scheme) - this just adds the InternalApiKey
    // scheme on top, LinkApi-only.
    public static WebApplicationBuilder AddJwtAndInternalApiKeyAuthentication(this WebApplicationBuilder builder)
    {
        builder.AddJwtBearerAuthentication()
            .AddScheme<AuthenticationSchemeOptions, InternalApiKeyAuthenticationHandler>(
                Constants.InternalApiKeyAuthenticationScheme, null);
        return builder;
    }
}
