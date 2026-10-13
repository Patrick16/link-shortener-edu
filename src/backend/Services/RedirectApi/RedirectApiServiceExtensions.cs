using Common;
using Infrastructure;

namespace RedirectApi;

// RedirectApi-only wiring - the parts of Program.cs that no other service shares. The click
// counter itself (AddClickCounter) moved to Shared/Infrastructure/RedisExtensions.cs - LinkApi
// now shares it too, to read the same counter back for GetLinks.
public static class RedirectApiServiceExtensions
{
    public static WebApplicationBuilder AddRedirectServices(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IEntityCacheService<Common.Models.Link>, LinkCacheService>();
        return builder;
    }
}
