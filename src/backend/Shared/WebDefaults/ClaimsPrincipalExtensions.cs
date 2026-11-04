using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace WebDefaults;

public static class ClaimsPrincipalExtensions
{
    // [Authorize] only guarantees a validated signature, not that Sub is present or parses as a
    // Guid. Every token AuthApi issues today carries Sub as a Guid string
    // (Services/AuthApi/JwtTokenGenerator.cs), so this never fails in practice - but an unguarded
    // Guid.Parse(User.FindFirstValue(...)!) on a missing/malformed claim throws
    // NullReference/FormatException, surfacing as an unhandled 500 instead of a clean 401 if that
    // ever stopped being true (e.g. a future token-shape change). Found in review.
    public static bool TryGetAuthenticatedUserId(this ClaimsPrincipal user, out Guid userId)
    {
        userId = Guid.Empty;
        var sub = user.FindFirstValue(JwtRegisteredClaimNames.Sub);
        return sub is not null && Guid.TryParse(sub, out userId);
    }
}
