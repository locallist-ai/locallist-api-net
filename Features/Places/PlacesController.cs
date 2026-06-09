using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LocalList.API.NET.Shared.Auth;
using LocalList.API.NET.Shared.Data;
using LocalList.API.NET.Shared.Dtos;
using LocalList.API.NET.Shared.I18n;
using LocalList.API.NET.Shared.Search;

namespace LocalList.API.NET.Features.Places;

// NOTA: el query param `?status=` en GET /places es ADMIN-ONLY. Para callers no-admin
// (incluidos anónimos) se fuerza silenciosamente `status = "published"` ignorando lo
// pedido — no devolvemos 403 para no confirmar la existencia del filtro interno.
[ApiController]
[Route("places")]
[AllowAnonymous]
public class PlacesController : ControllerBase
{
    private readonly LocalListDbContext _db;
    private readonly ILogger<PlacesController> _logger;
    private readonly LanguageAccessor _lang;

    public PlacesController(LocalListDbContext db, ILogger<PlacesController> logger, LanguageAccessor lang)
    {
        _db = db;
        _logger = logger;
        _lang = lang;
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

        // El filtro `status` solo lo honramos si el caller es admin.
        // Admin = Firebase RS256 + email @locallist.ai (via IsAdminCaller).
        // Cualquier otro caller —anónimo o app HS256— ve únicamente "published".
        var effectiveStatus = User.IsAdminCaller() ? (status ?? "published") : "published";

        var query = _db.Places.AsNoTracking().AsQueryable();

        query = query.Where(p => p.Status == effectiveStatus);

        if (!string.IsNullOrEmpty(city))
            query = query.Where(p => p.City == city);

        if (!string.IsNullOrEmpty(category))
            query = query.Where(p => p.Category == category);

        if (!string.IsNullOrEmpty(neighborhood))
            query = query.Where(p => p.Neighborhood == neighborhood);

        var escapedSearch = LikePatterns.Normalize(search);
        if (!string.IsNullOrEmpty(escapedSearch))
            query = query.Where(p => EF.Functions.ILike(p.Name, $"%{escapedSearch}%", @"\"));

        var total = await query.CountAsync(ct);

        var lang = _lang.Language;
        var places = await query
            .OrderBy(p => p.Name)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

        var placeDtos = places.Select(p => PlaceDto.FromEntity(p, lang)).ToList();

        return Ok(new
        {
            places = placeDtos,
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

        return Ok(PlaceDto.FromEntity(place, _lang.Language));
    }
}
