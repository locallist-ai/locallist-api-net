using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using LocalList.API.NET.Features.Builder.Services;
using LocalList.API.NET.Features.Favorites;
using LocalList.API.NET.Features.Cities;
using LocalList.API.NET.Features.Plans;
using LocalList.API.NET.Shared.Auth;
using LocalList.API.NET.Shared.Constants;
using LocalList.API.NET.Shared.Coverage;
using LocalList.API.NET.Shared.Data;
using LocalList.API.NET.Shared.Data.Entities;
using LocalList.API.NET.Shared.Dtos;
using LocalList.API.NET.Shared.I18n;
using LocalList.API.NET.Shared.Usage;

namespace LocalList.API.NET.Features.Import;

/// <summary>
/// F2 T4 — <c>POST /import/plan</c>: crea un plan a partir de un import de vídeo CONFIRMADO. Cierra
/// el flujo de F2: la app subió el vídeo (T1), recibió candidatos con matches (T3), el usuario
/// eligió cuáles quiere, y este endpoint materializa el plan con los places elegidos.
///
/// Reusa el máximo del pipeline existente: el <see cref="SchedulingService"/> DETERMINISTA reparte
/// el SET FIJO de places (sin RAG/ranking: el usuario ya eligió) en <c>days</c>, ordenando por
/// geografía y anclando comidas/nightlife con arrival-times sujetos a <c>opening_hours</c> y travel,
/// exactamente como un plan generado. Diferencia clave con la generación: allí el scheduler
/// SOBRE-selecciona candidatos y descartar unos pocos es inocuo (hay suplentes); aquí cada place es
/// una elección explícita del usuario, así que un place que el walk-clock descartaría por viabilidad
/// (cerrado ese día, hueco muerto, leg imposible, tope de día) NO se pierde: se RECONCILIA como
/// stop sin horario al final de su día. Invariante: el plan contiene SIEMPRE los N placeIds
/// confirmados (deduplicados), ni uno menos.
///
/// Gate: mismo patrón que T1 — <c>[Authorize]</c> AppScheme + Plus fresco de DB (<c>import_requires_plus</c>).
/// El import entero es feature Plus. La creación NO consume cuota de import (esa mide llamadas a
/// Gemini y aquí no hay ninguna), pero SÍ comparte el techo por IP <c>ImportLimit</c> (20/hr) con
/// T1: este endpoint acepta placeIds published arbitrarios SIN exigir un import previo, así que
/// sin él solo lo acotaría el global 100/min — paridad anti-farming con el resto del slice.
/// Gating de terceros idéntico a T1 (<c>Import:ThirdPartyEnabled</c>). Origen del plan:
/// <c>source="imported"</c> (nunca curated/showcase), visibilidad <c>private</c>, owner = caller.
/// </summary>
[ApiController]
[Route("import")]
[Authorize]
public class ImportPlanController : ControllerBase
{
    private const string TierPro = "pro";
    private const string SourceImported = "imported";
    private const int MaxCreatorHandleLength = 64;
    private const int MaxPlanNameLength = 120;

    private static readonly string[] AllowedPlatforms = { "self", "tiktok", "instagram", "other" };

    /// <summary>Regex conservadora tipo handle: '@' opcional + 1..63 chars de [A-Za-z0-9_.-]. Se guarda sin '@'.</summary>
    private static readonly Regex HandlePattern = new(@"^@?[A-Za-z0-9_.\-]{1,63}$", RegexOptions.Compiled);

    private readonly LocalListDbContext _db;
    private readonly SchedulingService _scheduler;
    private readonly ICityCoverageService _coverage;
    private readonly LanguageAccessor _lang;
    private readonly IConfiguration _config;
    private readonly ImportOptions _options;
    private readonly ILogger<ImportPlanController> _logger;

    public ImportPlanController(
        LocalListDbContext db,
        SchedulingService scheduler,
        ICityCoverageService coverage,
        LanguageAccessor lang,
        IConfiguration config,
        IOptions<ImportOptions> options,
        ILogger<ImportPlanController> logger)
    {
        _db = db;
        _scheduler = scheduler;
        _coverage = coverage;
        _lang = lang;
        _config = config;
        _options = options.Value;
        _logger = logger;
    }

    [HttpPost("plan")]
    [EnableRateLimiting("ImportLimit")]
    public async Task<IActionResult> CreatePlanFromImport(
        [FromBody] CreateImportPlanRequest request, CancellationToken ct)
    {
        // 1. Identidad fresca (App HS256 → Guid en sub; Firebase → lookup).
        var userId = await User.GetUserIdAsync(_db, ct);
        if (userId is null)
            return Unauthorized(new { error = "Invalid token claims." });

        // 2. Gate Plus — import es feature del catálogo Plus. Tier SIEMPRE fresco de DB (mismo
        //    patrón que T1: el claim del JWT vive 15 min y es forjable). SIN cuota nueva.
        var tier = await _db.Users
            .Where(u => u.Id == userId.Value)
            .Select(u => u.Tier)
            .FirstOrDefaultAsync(ct);
        if (tier is null)
            return Unauthorized(new { error = "Invalid token claims." });
        if (!string.Equals(tier, TierPro, StringComparison.Ordinal))
        {
            _logger.LogInformation("ImportPlan: denied, user {UserId} not Plus (tier={Tier})", userId, tier);
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "import_requires_plus" });
        }

        // 3. Gating de terceros — coherente con T1: platform ≠ self con el flag apagado → 403.
        var platform = NormalizePlatform(request.Platform);
        if (!string.Equals(platform, "self", StringComparison.Ordinal) && !_options.ThirdPartyEnabled)
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "third_party_import_disabled" });

        // 4. Ciudad en catálogo (cubierta). Mismo gate que /builder/chat y /chat/generate.
        var city = (request.City ?? string.Empty).Trim();
        if (!_coverage.IsLive(city))
        {
            _logger.LogInformation("ImportPlan: blocked, city not covered city={City}", city.Length == 0 ? "(empty)" : city);
            return BadRequest(new { error = "city_unsupported", city, liveCities = _coverage.LiveCities });
        }

        // 5. days: clamp a [1, MaxPlanDurationDays] (import es Plus → tope 14). Nunca rechaza por días.
        var days = Math.Clamp(request.Days, 1, PlanLimits.MaxPlanDurationDays);

        // 6. placeIds: dedup + orden CANÓNICO por Id, no vacío. El orden de envío del cliente NO
        //    participa: el plan resultante es función del SET de places (el scheduler reordena por
        //    geografía igualmente), así que dos confirmaciones del mismo set — en cualquier orden —
        //    producen exactamente el mismo plan.
        var placeIds = (request.PlaceIds ?? Array.Empty<Guid>()).Distinct().OrderBy(g => g).ToList();
        if (placeIds.Count == 0)
            return BadRequest(new { error = "import_invalid_places", message = "at least one placeId is required" });

        // Cap de stops razonable: MaxStopsPerDay × days (misma capacidad que un plan normal).
        var maxStops = PlanLimits.MaxStopsPerDay * days;
        if (placeIds.Count > maxStops)
            return BadRequest(new { error = "import_too_many_places", maxPlaces = maxStops });

        // 7. Validación ATÓMICA y OPACA de los places: TODOS deben existir, estar published y ser
        //    de la ciudad pedida (comparada normalizada). Si alguno falla → 400 estructurado SIN
        //    decir cuál (evita filtrar el catálogo) y SIN crear nada (aún no hemos tocado la DB).
        var places = await _db.Places
            .Where(p => placeIds.Contains(p.Id))
            .ToListAsync(ct);

        var normalizedCity = CityNameNormalizer.Normalize(city);
        var allValid = places.Count == placeIds.Count
            && places.All(p => p.Status == "published"
                               && CityNameNormalizer.Normalize(p.City) == normalizedCity);
        if (!allValid)
        {
            _logger.LogInformation(
                "ImportPlan: invalid places userId={UserId} requested={Req} found={Found} city={City}",
                userId, placeIds.Count, places.Count, city);
            return BadRequest(new { error = "import_invalid_places" });
        }

        // 8. Atribución de creador: saneada. Handle inválido → se DESCARTA (null), nunca se persiste
        //    sucio ni tumba la creación (la atribución es cosmética). Platform solo se persiste para
        //    orígenes de TERCEROS: un self-import no acredita a nadie externo.
        var creatorHandle = SanitizeCreatorHandle(request.CreatorHandle);
        var importedFromPlatform = string.Equals(platform, "self", StringComparison.Ordinal) ? null : platform;

        // 9. Nombre: si viene, saneado (trim + sin control + sin em-dash + cap); si queda vacío o no
        //    viene, fallback bilingüe localizado de PlanNamingService por el lang del request.
        var lang = LanguageAccessor.ResolveRequestLanguage(Request);
        var planName = SanitizePlanName(request.PlanName);
        if (string.IsNullOrEmpty(planName))
        {
            var prefs = new ExtractedPreferences { Days = days };
            planName = SanitizePlanName(PlanNamingService.BuildPlanName(prefs, city, string.Empty, lang))
                       ?? $"Plan · {city}";
        }

        // 10. Scheduling determinista sobre el SET FIJO. Los places se pasan en el orden CANÓNICO
        //     de placeIds (paso 6): la query de DB no garantiza orden y el scheduler es sensible al
        //     orden de entrada (anchor entre top-3 + round-robin), así que fijarlo hace el resultado
        //     DETERMINISTA e invariante al orden de envío. MaxStopsPerDay = tope global para que la
        //     SELECCIÓN no descarte ninguno (totalSlots ≥ placeIds.Count garantizado por el cap del
        //     paso 6).
        var placeById = places.ToDictionary(p => p.Id);
        var orderedPlaces = placeIds.Select(id => placeById[id]).ToList();

        var schedPrefs = new ExtractedPreferences
        {
            Days = days,
            MaxStopsPerDay = PlanLimits.MaxStopsPerDay,
            GroupType = "couple", // neutro (no-family → sin filtro de nightlife); el import no aporta grupo
        };
        var seed = ComputeSeed(placeIds, normalizedCity, days);
        var schedule = await _scheduler.BuildPlanScheduleAsync(orderedPlaces, schedPrefs, seed, ct);

        var now = DateTimeOffset.UtcNow;
        var plan = new Plan
        {
            Id = Guid.NewGuid(),
            Name = planName,
            City = orderedPlaces[0].City, // ortografía canónica del catálogo (todos comparten ciudad)
            Type = "custom",
            Source = SourceImported,   // NUNCA curated/showcase
            Description = null,
            DurationDays = days,
            Visibility = "private",    // default S0
            IsShowcase = false,
            CreatedById = userId.Value,
            CreatedAt = now,
            UpdatedAt = now,
            NameI18n = LanguageAccessor.SetI18nString(null, lang, planName),
            ImportedFromPlatform = importedFromPlatform,
            ImportedCreatorHandle = creatorHandle,
        };
        _db.Plans.Add(plan);

        // 11. Stops: primero los que el scheduler colocó (con horario/orden geográfico/travel), luego
        //     RECONCILIA los que descartó por viabilidad como stops sin horario al final de su día
        //     (round-robin), para no perder NUNCA un place confirmado por el usuario.
        var stops = BuildStops(plan.Id, placeIds, schedule, days);
        _db.PlanStops.AddRange(stops);

        // Plan + stops en UN SaveChanges = una transacción → atómico.
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (FavoritesController.IsForeignKeyViolation(ex))
        {
            // TOCTOU: hard-delete (admin) de un place entre el SELECT de validación y el INSERT
            // de los stops → 23503 del FK plan_stops→places. Mismo 400 opaco que si el place
            // nunca hubiera sido válido — nunca un 500. La transacción implícita del SaveChanges
            // se revierte entera (0 filas). Predicado compartido con Favorites (misma carrera).
            _logger.LogInformation(
                "ImportPlan: place hard-deleted mid-request userId={UserId} city={City}", userId, city);
            return BadRequest(new { error = "import_invalid_places" });
        }

        _logger.LogInformation(
            "ImportPlan: created plan={PlanId} userId={UserId} city={City} days={Days} places={N} platform={Platform}",
            plan.Id, userId, plan.City, days, placeIds.Count, platform);

        // 12. Respuesta: el PlanDetailDto del plan creado (como el flujo normal de creación) para que
        //     la app navegue directa al plan. Recargamos con stops + places para agrupar por días.
        var created = await _db.Plans.AsNoTracking()
            .Include(p => p.Stops)
            .ThenInclude(s => s.Place)
            .FirstAsync(p => p.Id == plan.Id, ct);

        return Created($"/plans/{plan.Id}",
            PlanDetailDto.FromEntity(created, lang, null, _config["Api:PublicBaseUrl"]));
    }

    // ── Stops: schedule + reconcile ──────────────────────────────────────────────

    /// <summary>
    /// Convierte el resultado del scheduler en <see cref="PlanStop"/> y RECONCILIA los places
    /// confirmados que el walk-clock descartó (cerrado/hueco/leg/tope): se añaden sin horario al
    /// final de su día, repartidos round-robin. Determinista dado el orden estable de placeIds.
    /// </summary>
    private static List<PlanStop> BuildStops(
        Guid planId, List<Guid> placeIds, ScheduleResult schedule, int days)
    {
        var stops = schedule.Stops
            .Select(sd => new PlanStop
            {
                Id = Guid.NewGuid(),
                PlanId = planId,
                PlaceId = sd.PlaceId,
                DayNumber = sd.DayNumber,
                OrderIndex = sd.OrderIndex,
                TimeBlock = sd.TimeBlock,
                SuggestedArrival = string.IsNullOrEmpty(sd.SuggestedArrival) ? null : TimeSpan.Parse(sd.SuggestedArrival),
                SuggestedDurationMin = sd.SuggestedDurationMin,
                TravelFromPrevious = sd.TravelFromPrevious is null
                    ? null
                    : System.Text.Json.JsonSerializer.SerializeToDocument(sd.TravelFromPrevious),
            })
            .ToList();

        // Reconciliar los descartados: siguiente order_index por día tras los ya colocados.
        var scheduled = stops.Select(s => s.PlaceId).ToHashSet();
        var nextOrder = new Dictionary<int, int>();
        foreach (var s in stops)
            nextOrder[s.DayNumber] = Math.Max(nextOrder.GetValueOrDefault(s.DayNumber, -1), s.OrderIndex);

        var dropped = placeIds.Where(id => !scheduled.Contains(id)).ToList();
        for (int i = 0; i < dropped.Count; i++)
        {
            var day = (i % days) + 1; // reparto round-robin estable
            var order = nextOrder.GetValueOrDefault(day, -1) + 1;
            nextOrder[day] = order;
            stops.Add(new PlanStop
            {
                Id = Guid.NewGuid(),
                PlanId = planId,
                PlaceId = dropped[i],
                DayNumber = day,
                OrderIndex = order,
                // Sin horario/travel: no es viable con seguridad, pero no se pierde (contrato de manual).
            });
        }

        return stops;
    }

    // ── Sanitización / helpers ───────────────────────────────────────────────────

    private static string NormalizePlatform(string? raw)
    {
        var p = (raw ?? "self").Trim().ToLowerInvariant();
        if (p.Length == 0) return "self";
        return AllowedPlatforms.Contains(p) ? p : "other";
    }

    /// <summary>Valida contra <see cref="HandlePattern"/> y guarda sin '@'. Inválido/ausente → null.</summary>
    private static string? SanitizeCreatorHandle(string? raw)
    {
        var s = raw?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        if (!HandlePattern.IsMatch(s)) return null;
        s = s.TrimStart('@');
        if (s.Length == 0 || s.Length > MaxCreatorHandleLength) return null;
        return s;
    }

    /// <summary>Trim + quita control chars + neutraliza em/en-dash + cap. Vacío → null (usa fallback).</summary>
    private static string? SanitizePlanName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var clean = new string(raw.Where(c => !char.IsControl(c)).ToArray())
            .Replace("—", "-").Replace("–", "-")
            .Trim();
        if (clean.Length > MaxPlanNameLength) clean = clean[..MaxPlanNameLength].Trim();
        return clean.Length == 0 ? null : clean;
    }

    /// <summary>
    /// Semilla estable (FNV-1a 32-bit) del request: mismos placeIds (ordenados) + ciudad + días →
    /// misma semilla → mismo scheduling. Ordena los ids para que el orden de confirmación no altere
    /// el resultado. Estable entre procesos (a diferencia de string.GetHashCode).
    /// </summary>
    private static int ComputeSeed(List<Guid> placeIds, string normalizedCity, int days)
    {
        var canonical = string.Join("|",
            days.ToString(),
            normalizedCity,
            string.Join(",", placeIds.OrderBy(g => g).Select(g => g.ToString("N"))));
        unchecked
        {
            uint hash = 2166136261;
            foreach (var c in canonical)
            {
                hash ^= c;
                hash *= 16777619;
            }
            return (int)(hash & int.MaxValue);
        }
    }
}
