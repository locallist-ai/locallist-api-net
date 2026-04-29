using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using LocalList.API.NET.Shared.Auth;
using LocalList.API.NET.Shared.Data;

namespace LocalList.API.NET.Features.Admin.Cities;

[ApiController]
[Route("admin/cities")]
[AdminAuthorize]
[EnableRateLimiting("AdminLimit")]
public class AdminCitiesController : ControllerBase
{
    private readonly LocalListDbContext _db;
    private readonly ILogger<AdminCitiesController> _logger;

    public AdminCitiesController(LocalListDbContext db, ILogger<AdminCitiesController> logger)
    {
        _db = db;
        _logger = logger;
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteCity(Guid id, CancellationToken ct = default)
    {
        var city = await _db.Cities.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (city == null) return NotFound(new { error = "City not found" });

        _db.Cities.Remove(city);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Admin deleted city {CityId} ({Name})", city.Id, city.Name);
        return NoContent();
    }
}
