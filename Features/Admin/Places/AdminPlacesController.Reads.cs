using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LocalList.API.NET.Shared.Search;

namespace LocalList.API.NET.Features.Admin.Places;

// Cities lookup + place list/detail reads. Logic is identical to the original single-file
// version; only its location changed.
public partial class AdminPlacesController
{
    [HttpGet("cities")]
    public async Task<IActionResult> GetCities(CancellationToken ct)
    {
        var cities = await _db.Places
            .Where(p => p.City != null)
            .Select(p => p.City)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync(ct);

        return Ok(new { cities });
    }

    [HttpGet]
    public async Task<IActionResult> GetPlaces(
        [FromQuery] string? status,
        [FromQuery] string? city,
        [FromQuery] string? category,
        [FromQuery] string? search,
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 200);

        var query = _db.Places.AsNoTracking().AsQueryable();

        if (!string.IsNullOrEmpty(status))
            query = query.Where(p => p.Status == status);

        if (!string.IsNullOrEmpty(city))
            query = query.Where(p => p.City == city);

        if (!string.IsNullOrEmpty(category))
            query = query.Where(p => p.Category == category);

        var escapedSearch = LikePatterns.Normalize(search);
        if (!string.IsNullOrEmpty(escapedSearch))
            query = query.Where(p => EF.Functions.ILike(p.Name, $"%{escapedSearch}%", @"\"));

        var total = await query.CountAsync(ct);

        var ordered = status == "in_review"
            ? query.OrderBy(p => p.ReviewDeferredAt == null)
                   .ThenByDescending(p => p.ReviewDeferredAt)
                   .ThenByDescending(p => p.CreatedAt)
            : query.OrderByDescending(p => p.CreatedAt);

        var places = await ordered
            .Skip(offset)
            .Take(limit)
            .Select(p => AdminPlaceDto.FromEntity(p))
            .ToListAsync(ct);

        return Ok(new { places, total });
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetPlace(Guid id, CancellationToken ct)
    {
        var place = await _db.Places.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id, ct);

        if (place == null)
            return NotFound(new { error = "Place not found" });

        return Ok(AdminPlaceDto.FromEntity(place));
    }
}
