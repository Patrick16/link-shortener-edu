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
public partial class ReportsController(IClickFactQueryService queryService, IClickMetaQueryService clickMetaQueryService) : ControllerBase
{
    private readonly IClickFactQueryService _queryService = queryService;
    private readonly IClickMetaQueryService _clickMetaQueryService = clickMetaQueryService;

    // Bounds for GetRecentClicks's ?limit - a missing/zero/negative value falls back to
    // DefaultClicksLimit, anything above MaxClicksLimit is clamped rather than rejected, so a caller
    // passing an unreasonable value just gets the capped list instead of a 400.
    private const int DefaultClicksLimit = 20;
    private const int MaxClicksLimit = 100;

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

        // Always 200: GetSummaryAsync already scopes every underlying query to this caller's own
        // user_id, so a hash this caller doesn't own comes back as an ordinary all-zero summary -
        // the same shape a hash that doesn't exist at all gets - rather than this controller adding
        // its own 404 branch on top (see .notes/PLAN.md's Owner-scoped reporting entry for the full
        // reasoning, and IClickFactQueryService's own comment for why an earlier 404-for-someone-
        // else's-hash branch was itself the privacy leak it was meant to prevent).
        var summary = await _queryService.GetSummaryAsync(hash, userId, cancellationToken);
        return Ok(summary);
    }

    // Mongo-backed counterpart to GetSummary - raw per-click documents (exact city, full user
    // agent, referrer URL) instead of ClickHouse's pre-aggregated buckets. Same owner-scoping and
    // always-200 reasoning as GetSummary - see IClickMetaQueryService.GetRecentAsync's own comment.
    [HttpGet("{hash}/clicks")]
    public async Task<IActionResult> GetRecentClicks(
        [FromRoute] string hash, [FromQuery] int limit, CancellationToken cancellationToken)
    {
        if (!HashFormat().IsMatch(hash))
        {
            return Problem(detail: "Hash is not a valid short-link hash.", statusCode: StatusCodes.Status400BadRequest);
        }

        var userId = Guid.Parse(User.FindFirstValue(JwtRegisteredClaimNames.Sub)!);
        var effectiveLimit = Math.Clamp(limit <= 0 ? DefaultClicksLimit : limit, 1, MaxClicksLimit);

        var clicks = await _clickMetaQueryService.GetRecentAsync(hash, userId, effectiveLimit, cancellationToken);
        return Ok(clicks);
    }
}
