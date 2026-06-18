using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using LocalList.API.NET.Shared.Auth;
using LocalList.API.NET.Shared.Coverage;
using LocalList.API.NET.Shared.Data;
using LocalList.API.NET.Shared.Data.Entities;
using LocalList.API.NET.Shared.Search;

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
///
/// Hardenings post-audit 2026-04-27 (multi-agent review):
///   - Length cap (64) + control/format char strip en NormalizeName (DoS guard).
///   - Rate limits dedicados (CitySearchLimit / CityCreateLimit).
///   - POST idempotency catches DbUpdateException 23505 (race-safe).
///   - Reject empty q (avoid full-table sort + DoS lever).
/// </summary>
[ApiController]
[Route("cities")]
public class CitiesController : ControllerBase
{
    private readonly LocalListDbContext _db;
    private readonly ILogger<CitiesController> _logger;
    private readonly ICityCoverageService _coverage;

    private const int MinSearchLength = 2;

    public CitiesController(LocalListDbContext db, ILogger<CitiesController> logger, ICityCoverageService coverage)
    {
        _db = db;
        _logger = logger;
        _coverage = coverage;
    }

    /// <summary>
    /// Ciudades LIVE (allowlist <c>Coverage:LiveCities</c>). La app pinta el
    /// selector solo con estas; <c>/cities/search</c> sigue sirviendo el registry
    /// completo (autocomplete) y no debe usarse para el gate de cobertura.
    ///
    /// Enriquece cada nombre con su fila seed (Id/country) si existe, pero la
    /// fuente de verdad es la allowlist, no la tabla: una ciudad LIVE sin fila
    /// seed igualmente se devuelve (con Id null).
    /// </summary>
    [HttpGet("live")]
    [AllowAnonymous]
    public async Task<IActionResult> Live(CancellationToken ct)
    {
        var liveNames = _coverage.LiveCities;
        if (liveNames.Count == 0)
            return Ok(new { cities = Array.Empty<LiveCityDto>() });

        var normalized = liveNames.Select(CityNameNormalizer.Normalize).ToList();

        // Una sola query: trae las filas seed cuyo normalized esté en la allowlist.
        var seeds = await _db.Cities
            .Where(c => c.Source == "seed" && normalized.Contains(c.NormalizedName))
            .ToListAsync(ct);

        var cities = liveNames.Select(name =>
        {
            var match = seeds.FirstOrDefault(s => s.NormalizedName == CityNameNormalizer.Normalize(name));
            return new LiveCityDto
            {
                Id = match?.Id,
                Name = match?.Name ?? name,
                Country = match?.Country,
            };
        }).ToList();

        return Ok(new { cities });
    }

    /// <summary>
    /// Compatibility wrapper. Existing tests and any external callers that
    /// imported <c>CitiesController.NormalizeName</c> continue to work; nuevos
    /// consumidores deben usar <see cref="CityNameNormalizer.Normalize"/>.
    /// </summary>
    public static string NormalizeName(string raw) => CityNameNormalizer.Normalize(raw);

    [HttpGet("search")]
    [AllowAnonymous]
    [EnableRateLimiting("CitySearchLimit")]
    public async Task<IActionResult> Search([FromQuery] string? q, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q))
            return BadRequest(new { error = "q is required" });
        if (q.Length > CityNameNormalizer.MaxRawLength)
            return BadRequest(new { error = $"q must be {CityNameNormalizer.MaxRawLength} chars or fewer" });

        var queryNorm = CityNameNormalizer.Normalize(q);
        if (queryNorm.Length < MinSearchLength)
            return BadRequest(new { error = $"q must contain at least {MinSearchLength} usable characters" });

        var escapedNorm = LikePatterns.Escape(queryNorm);

        // Seeds visibles para todos; ciudades de usuario solo para su creador.
        Guid? currentUserId = null;
        if (User.Identity?.IsAuthenticated == true)
            currentUserId = await User.GetUserIdAsync(_db, ct);

        var matches = await _db.Cities
            .Where(c => EF.Functions.ILike(c.NormalizedName, $"{escapedNorm}%", @"\")
                        && (c.Source == "seed" || (currentUserId != null && c.CreatedById == currentUserId)))
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
    [EnableRateLimiting("CityCreateLimit")]
    public async Task<IActionResult> Create([FromBody] CreateCityRequest request, CancellationToken ct)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "name is required" });

        var name = request.Name.Trim();
        if (name.Length < 3 || name.Length > CityNameNormalizer.MaxRawLength)
            return BadRequest(new { error = $"name length must be between 3 and {CityNameNormalizer.MaxRawLength}" });

        var normalized = CityNameNormalizer.Normalize(name);
        if (normalized.Length == 0)
            return BadRequest(new { error = "name has no usable characters" });

        if (!CityNameValidator.IsLikelyRealCity(normalized, out var rejectReason))
            return BadRequest(new { error = rejectReason });

        // Si ya existe (case/accent insensitive), devolver la existente. Esto
        // hace el endpoint idempotente — POST con nombre duplicado no rompe.
        var existing = await _db.Cities
            .FirstOrDefaultAsync(c => c.NormalizedName == normalized, ct);
        if (existing != null)
            return Ok(MapDto(existing));

        var userId = await User.GetUserIdAsync(_db, ct);
        // Token válido pero user no en DB: tratar como Unauthorized en vez de
        // crear una fila huérfana con CreatedById=null. Inconsistente con el
        // resto del codebase (PlansController:28-29) si no se valida aquí.
        if (userId == null)
            return Unauthorized(new { error = "Invalid token" });

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

        try
        {
            _db.Cities.Add(city);
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Race: otra request creó la misma normalized entre el check y el
            // insert. Re-query y devolver la fila ganadora — preserva la
            // idempotencia bajo concurrencia.
            _db.Entry(city).State = EntityState.Detached;
            var winner = await _db.Cities
                .FirstOrDefaultAsync(c => c.NormalizedName == normalized, ct);
            if (winner != null)
                return Ok(MapDto(winner));
            // Inesperado — unique violation pero la fila no aparece. Re-throw.
            throw;
        }

        _logger.LogInformation("User {UserId} created new city {City} ({Normalized})",
            userId, city.Name, city.NormalizedName);

        return CreatedAtAction(
            nameof(Search),
            null,
            MapDto(city));
    }

    private static CityDto MapDto(City c) => new()
    {
        Id = c.Id,
        Name = c.Name,
        Country = c.Country,
        Source = c.Source,
    };

    /// <summary>
    /// Detecta unique constraint violations en Postgres (SqlState "23505")
    /// y en EF in-memory tests (que no tienen Npgsql). En in-memory el
    /// inner exception es null y el mensaje contiene "unique".
    /// </summary>
    private static bool IsUniqueViolation(DbUpdateException ex)
    {
        if (ex.InnerException is PostgresException pg && pg.SqlState == PostgresErrorCodes.UniqueViolation)
            return true;
        // EF in-memory: el provider valida el unique index y throw DbUpdateException
        // con mensaje "unique" sin Postgres inner.
        return ex.Message.Contains("unique", StringComparison.OrdinalIgnoreCase)
            || (ex.InnerException?.Message.Contains("unique", StringComparison.OrdinalIgnoreCase) ?? false);
    }
}

public class CityDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Country { get; set; }
    public string Source { get; set; } = "user";
}

/// <summary>
/// Ciudad LIVE para el selector de la app. <see cref="Id"/> es nullable: una
/// ciudad de la allowlist puede no tener fila seed todavía.
/// </summary>
public class LiveCityDto
{
    public Guid? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Country { get; set; }
}

public class CreateCityRequest
{
    [Required]
    [MaxLength(64)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(60)]
    public string? Country { get; set; }
}
