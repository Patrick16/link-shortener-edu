using Common;
using Common.Models;
using LinkApi.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LinkApi.Controllers;

[ApiController]
[Route("[controller]")]
public class LinksController(
    IDbContextFactory<DatabaseContext> factory,
    IEntityCacheService<Link> service) : Controller
{
    private readonly IDbContextFactory<DatabaseContext> _contextFactory = factory;
    private readonly IEntityCacheService<Link> _service = service;

    [HttpPost]
    public async Task<ActionResult<LinkResponse>> CreateLink(
        [FromBody] LinkCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        
        return await Task.FromResult(new LinkResponse("shorten", DateTime.UtcNow));
    }

    [HttpGet("{hash}")]
    public async Task<ActionResult<LinkResponse>> GetLink(
        [FromRoute] string hash,CancellationToken cancellationToken)
    {
        async Task<Link> Fetch()
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var res = await context.Links.FirstOrDefaultAsync(x => x.Hash == hash, cancellationToken);
            return res;
        }
        var res = await _service.GetOrFetch(hash, fetchFromDb: Fetch, cancellationToken);
        if(res is null)
        {
            return NotFound();
        }

        return new LinkResponse(res.ShortenLink, res.CreatedAt);
    }
}
