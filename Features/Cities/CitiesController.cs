using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LocalList.API.NET.Shared.Auth;
using LocalList.API.NET.Shared.Data;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.NET.Features.Cities;

/// <summary>
/// Registro de ciudades — Pablo 2026-04-27. El custom builder permite teclear
/// cualquier ciudad; este controller expone autocomplete + creación on-demand.
///
/// Endpoints:
///   GET /cities/search?q=miam — match prefix sobre NormalizedName, top 10.
///   POST /cities { name, country? } — registra nueva ciudad si no existe.
///
/// La tabla es un registry, no FK. Plan.City sigue siendo string libre por
/// retro-compat con la data histórica. Esto facilita que cualquier cliente
/// pueda crear planes con cualquier ciudad sin bloquearse.
/// </summary>
[ApiController]
[Route("cities")]
public class CitiesController : ControllerBase
{
    private readonly LocalListDbContext _db;
    private readonly ILogger<CitiesController> _logger;

    public CitiesController(LocalListDbContext db, ILogger<CitiesController> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Normaliza un nombre a lowercase ASCII sin acentos para unique constraint
    /// + matching consistente. Pablo 2026-04-27: "Málaga" → "malaga", "São
    /// Paulo" → "sao paulo".
    /// </summary>
    public static string NormalizeName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var trimmed = raw.Trim();
        var formD = trimmed.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(formD.Length);
        foreach (var ch in formD)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }

    [HttpGet("search")]
    [AllowAnonymous]
    public async Task<IActionResult> Search([FromQuery] string? q, CancellationToken ct)
    {
        var queryNorm = NormalizeName(q ?? string.Empty);
        IQueryable<City> query = _db.Cities;
        if (queryNorm.Length > 0)
        {
            // Prefix match — para autocomplete típico ("mia" → Miami).
            query = query.Where(c => c.NormalizedName.StartsWith(queryNorm));
        }
        var matches = await query
            .OrderBy(c => c.Name)
            .Take(10)
            .Select(c => new CityDto
            {
                Id = c.Id,
                Name = c.Name,
                Country = c.Country,
                Source = c.Source,
            })
            .ToListAsync(ct);
        return Ok(new { cities = matches });
    }

    [HttpPost]
    [Authorize]
    public async Task<IActionResult> Create([FromBody] CreateCityRequest request, CancellationToken ct)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "name is required" });

        var name = request.Name.Trim();
        if (name.Length < 2 || name.Length > 60)
            return BadRequest(new { error = "name length must be between 2 and 60" });

        var normalized = NormalizeName(name);
        if (normalized.Length == 0)
            return BadRequest(new { error = "name has no usable characters" });

        // Si ya existe (case/accent insensitive), devolver la existente. Esto
        // hace el endpoint idempotente — POST con nombre duplicado no rompe.
        var existing = await _db.Cities
            .FirstOrDefaultAsync(c => c.NormalizedName == normalized, ct);
        if (existing != null)
        {
            return Ok(new CityDto
            {
                Id = existing.Id,
                Name = existing.Name,
                Country = existing.Country,
                Source = existing.Source,
            });
        }

        var userId = await User.GetUserIdAsync(_db, ct);

        var city = new City
        {
            Id = Guid.NewGuid(),
            Name = name,
            NormalizedName = normalized,
            Country = string.IsNullOrWhiteSpace(request.Country) ? null : request.Country.Trim(),
            Source = "user",
            CreatedById = userId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.Cities.Add(city);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("User {UserId} created new city {City} ({Normalized})",
            userId, city.Name, city.NormalizedName);

        return CreatedAtAction(
            nameof(Search),
            null,
            new CityDto { Id = city.Id, Name = city.Name, Country = city.Country, Source = city.Source });
    }
}

public class CityDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Country { get; set; }
    public string Source { get; set; } = "user";
}

public class CreateCityRequest
{
    [Required]
    [MaxLength(60)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(60)]
    public string? Country { get; set; }
}
