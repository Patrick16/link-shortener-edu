using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ReportingApi.Controllers;

// Every action here requires a valid Bearer token (see Program.cs's AddJwtBearerAuthentication) -
// unlike LinkApi/RedirectApi, there is no anonymous path through this controller at all.
[Authorize]
[ApiController]
[Route("reports")]
public partial class ReportsController(IClickFactQueryService queryService) : ControllerBase
{
    private readonly IClickFactQueryService _queryService = queryService;

    // Matches the short-hash alphabet Sha256Base62HashGenerator produces - rejecting anything else
    // here means the ClickHouse query never even runs on garbage input, on top of the server-side
    // {hash:String} parameter substitution already ruling out injection either way.
    [GeneratedRegex("^[A-Za-z0-9]{1,16}$")]
    private static partial Regex HashFormat();

    [HttpGet("{hash}/summary")]
    public async Task<IActionResult> GetSummary([FromRoute] string hash, CancellationToken cancellationToken)
    {
        if (!HashFormat().IsMatch(hash))
        {
            return Problem(detail: "Hash is not a valid short-link hash.", statusCode: StatusCodes.Status400BadRequest);
        }

        // [Authorize] already guarantees a valid Bearer token, so Sub is always present and
        // parseable - same guarantee LinksController relies on for its own owner checks.
        var userId = Guid.Parse(User.FindFirstValue(JwtRegisteredClaimNames.Sub)!);

        var summary = await _queryService.GetSummaryAsync(hash, userId, cancellationToken);
        // 404, not 403 - a hash this caller doesn't own gets the same answer as one that doesn't
        // exist at all, so a caller can't use this endpoint to probe which hashes belong to other
        // users (see .notes/PLAN.md's Owner-scoped reporting entry for the full reasoning).
        return summary is null ? NotFound() : Ok(summary);
    }
}
