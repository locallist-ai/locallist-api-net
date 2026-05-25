using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using LocalList.API.NET.Shared.Taxonomy;

namespace LocalList.API.NET.Features.Taxonomy;

[ApiController]
[Route("taxonomy")]
[AllowAnonymous]
public class TaxonomyController : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(
        [FromServices] ITaxonomyService taxonomy,
        CancellationToken ct)
    {
        var lastUpdated = await taxonomy.GetLastUpdatedAsync(ct);
        var etag = $"\"{lastUpdated.UtcTicks:x}\"";

        if (Request.Headers.IfNoneMatch.Contains(etag))
            return StatusCode(304);

        var all = await taxonomy.GetAllAsync(ct);

        var subcategoriesByCategory = all
            .GroupBy(s => s.CategoryKey)
            .ToDictionary(g => g.Key, g => g.Select(s => s.Key).ToList());

        var labelsEn = all.ToDictionary(
            s => $"{s.CategoryKey}.{s.Key}",
            s => s.LabelEn);

        var labelsEs = all.ToDictionary(
            s => $"{s.CategoryKey}.{s.Key}",
            s => s.LabelEs);

        Response.Headers.ETag = etag;
        Response.Headers.CacheControl = "public, max-age=3600";

        return Ok(new
        {
            categories = PlaceTaxonomy.Categories,
            subcategoriesByCategory,
            labels = new { en = labelsEn, es = labelsEs }
        });
    }
}
