using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LocalList.API.NET.Data;

namespace LocalList.API.NET.Controllers;

[ApiController]
[Route("places")]
public class PlacesController : ControllerBase
{
    private readonly LocalListDbContext _db;

    public PlacesController(LocalListDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> GetPlaces(
        [FromQuery] string? city,
        [FromQuery] string? category,
        [FromQuery] string? neighborhood,
        [FromQuery] string? status = "published",
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0)
    {
        var query = _db.Places.AsQueryable();

        query = query.Where(p => p.Status == status);

        if (!string.IsNullOrEmpty(city))
            query = query.Where(p => p.City == city);

        if (!string.IsNullOrEmpty(category))
            query = query.Where(p => p.Category == category);

        if (!string.IsNullOrEmpty(neighborhood))
            query = query.Where(p => p.Neighborhood == neighborhood);

        var places = await query
            .OrderBy(p => p.Name)
            .Skip(offset)
            .Take(limit)
            .ToListAsync();

        return Ok(new
        {
            places,
            total = places.Count
        });
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetPlace(Guid id)
    {
        var place = await _db.Places.FirstOrDefaultAsync(p => p.Id == id);

        if (place == null)
            return NotFound(new { error = "Place not found" });

        return Ok(place);
    }
}
