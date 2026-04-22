using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LocalList.API.NET.Shared.Auth;
using LocalList.API.NET.Shared.Data;

namespace LocalList.API.NET.Features.Places;

// NOTA: el query param `?status=` en GET /places es ADMIN-ONLY. Para callers no-admin
// (incluidos anónimos) se fuerza silenciosamente `status = "published"` ignorando lo
// pedido — no devolvemos 403 para no confirmar la existencia del filtro interno.
[ApiController]
[Route("places")]
[AllowAnonymous]
public class PlacesController : ControllerBase
{
    private const string AdminEmailDomain = "@locallist.ai";

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
        [FromQuery] string? search,
        [FromQuery] string? status = "published",
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 100);

        // El filtro `status` solo lo honramos si el caller es admin (@locallist.ai).
        // Cualquier otro caller —anónimo o usuario autenticado normal— ve únicamente
        // places en "published" sin importar el valor que haya pedido.
        // Igual que en AdminAuthorizationFilter: admin = autenticado + email bajo @locallist.ai.
        var email = User.GetEmail();
        var isAdmin = User.Identity?.IsAuthenticated == true
            && !string.IsNullOrEmpty(email)
            && email.EndsWith(AdminEmailDomain, StringComparison.OrdinalIgnoreCase);

        var effectiveStatus = isAdmin ? (status ?? "published") : "published";

        var query = _db.Places.AsNoTracking().AsQueryable();

        query = query.Where(p => p.Status == effectiveStatus);

        if (!string.IsNullOrEmpty(city))
            query = query.Where(p => p.City == city);

        if (!string.IsNullOrEmpty(category))
            query = query.Where(p => p.Category == category);

        if (!string.IsNullOrEmpty(neighborhood))
            query = query.Where(p => p.Neighborhood == neighborhood);

        if (!string.IsNullOrEmpty(search))
            query = query.Where(p => EF.Functions.ILike(p.Name, $"%{search}%"));

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
