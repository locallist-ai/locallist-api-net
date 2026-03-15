using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LocalList.API.NET.Shared.Data;

namespace LocalList.API.NET.Features.Places;

[ApiController]
[Route("places")]
[AllowAnonymous]
public class PlacesController : ControllerBase
{
    private readonly LocalListDbContext _db;
    private readonly ILogger<PlacesController> _logger;

    public PlacesController(LocalListDbContext db, ILogger<PlacesController> logger)
    {
        _db = db;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetPlaces(
        [FromQuery] string? city,
        [FromQuery] string? category,
        [FromQuery] string? neighborhood,
        [FromQuery] string? status = "published",
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 100);
        // Prevent anonymous users from bypassing draft/review filters
        var isAnonymous = !User.Identity?.IsAuthenticated ?? true;
        if (isAnonymous && status != "published")
        {
            _logger.LogWarning("Anonymous user attempted to access {Status} places", status);
            return Unauthorized(new { error = "Only authenticated curators can view non-published places." });
        }

        var query = _db.Places.AsNoTracking().AsQueryable();

        query = query.Where(p => p.Status == status);

        if (!string.IsNullOrEmpty(city))
            query = query.Where(p => p.City == city);

        if (!string.IsNullOrEmpty(category))
            query = query.Where(p => p.Category == category);

        if (!string.IsNullOrEmpty(neighborhood))
            query = query.Where(p => p.Neighborhood == neighborhood);

        var total = await query.CountAsync(ct);

        var places = await query
            .OrderBy(p => p.Name)
            .Skip(offset)
            .Take(limit)
            .Select(p => PlaceDto.FromEntity(p))
            .ToListAsync(ct);

        return Ok(new
        {
            places,
            total
        });
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetPlace(Guid id, CancellationToken ct)
    {
        var isAnonymous = !User.Identity?.IsAuthenticated ?? true;

        var place = await _db.Places.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id, ct);

        if (place == null)
            return NotFound(new { error = "Place not found" });

        // Anonymous users can only see published places
        if (isAnonymous && place.Status != "published")
            return NotFound(new { error = "Place not found" });

        return Ok(PlaceDto.FromEntity(place));
    }
}
