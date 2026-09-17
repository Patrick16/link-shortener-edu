using Common;
using Common.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace RedirectApi.Controllers;

[ApiController]
[Route("/")]
public class RedirectController(
    DatabaseContext context,
    IEntityCacheService<Link> cache) : Controller
{
    private readonly DatabaseContext _context = context;
    private readonly IEntityCacheService<Link> _cache = cache;

    [HttpGet("{hash}")]
    public async Task<IActionResult> RedirectToOrigin(
        [FromRoute] string hash,
        CancellationToken cancellationToken)
    {
        async Task<Link?> Fetch() =>
            await _context.Links.FirstOrDefaultAsync(x => x.Hash == hash, cancellationToken);

        var link = await _cache.GetOrFetch(hash, fetchFromDb: Fetch, cancellationToken);
        if (link is null)
        {
            return NotFound();
        }

        // 302 — a short link's target can change (or the link could be deleted), so this must
        // never be cached as permanent by the browser.
        return Redirect(link.OriginalLink);
    }
}
