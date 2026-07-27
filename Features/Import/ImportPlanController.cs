using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using LocalList.API.NET.Features.Plans;
using LocalList.API.NET.Shared.AI;
using LocalList.API.NET.Shared.AI.Services;
using LocalList.API.NET.Shared.Auth;
using LocalList.API.NET.Shared.Constants;
using LocalList.API.NET.Shared.Coverage;
using LocalList.API.NET.Shared.Data;
using LocalList.API.NET.Shared.Dtos;
using LocalList.API.NET.Shared.I18n;
using LocalList.API.NET.Shared.Usage;

namespace LocalList.API.NET.Features.Import;

/// <summary>
/// F2 T4 — <c>POST /import/plan</c>: crea un plan a partir de un import de vídeo CONFIRMADO. Cierra
/// el flujo de F2: la app subió el vídeo (T1), recibió candidatos con matches (T3), el usuario
/// eligió cuáles quiere, y este endpoint materializa el plan con los places elegidos.
///
/// Reusa el máximo del pipeline existente: el scheduler DETERMINISTA (<see cref="ImportPlanService"/>)
/// reparte el SET FIJO de places (sin RAG/ranking: el usuario ya eligió) en <c>days</c>, ordenando por
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
[Authorize(AuthenticationSchemes = AuthSchemes.App)]
public class ImportPlanController : ControllerBase
{
    private const int MaxPlanNameLength = 120;

    private readonly LocalListDbContext _db;
    private readonly ImportPlanService _planService;
    private readonly IPlanNamingService _naming;
    private readonly ICityCoverageService _coverage;
    private readonly IConfiguration _config;
    private readonly ImportOptions _options;
    private readonly ILogger<ImportPlanController> _logger;

    public ImportPlanController(
        LocalListDbContext db,
        ImportPlanService planService,
        IPlanNamingService naming,
        ICityCoverageService coverage,
        IConfiguration config,
        IOptions<ImportOptions> options,
        ILogger<ImportPlanController> logger)
    {
        _db = db;
        _planService = planService;
        _naming = naming;
        _coverage = coverage;
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
        var tier = await TierGate.GetFreshTierAsync(_db, userId.Value, ct);
        if (tier is null)
            return Unauthorized(new { error = "Invalid token claims." });
        if (!TierGate.IsPro(tier))
        {
            _logger.LogInformation("ImportPlan: denied, user {UserId} not Plus (tier={Tier})", userId, tier);
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "import_requires_plus" });
        }

        // 3. Gating de terceros — coherente con T1: platform ≠ self con el flag apagado → 403.
        var platform = ImportAttribution.NormalizePlatform(request.Platform);
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

        // 7. Atribución de creador: saneada. Handle inválido → se DESCARTA (null), nunca se persiste
        //    sucio ni tumba la creación (la atribución es cosmética). Platform solo se persiste para
        //    orígenes de TERCEROS: un self-import no acredita a nadie externo.
        var creatorHandle = ImportAttribution.SanitizeCreatorHandle(request.CreatorHandle);
        var importedFromPlatform = string.Equals(platform, "self", StringComparison.Ordinal) ? null : platform;

        // 8. Nombre: si viene, saneado (trim + sin control + sin em-dash + cap); si queda vacío o no
        //    viene, fallback bilingüe localizado del naming por el lang del request.
        var lang = LanguageAccessor.ResolveRequestLanguage(Request);
        var planName = SanitizePlanName(request.PlanName);
        if (string.IsNullOrEmpty(planName))
        {
            var prefs = new ExtractedPreferences { Days = days };
            planName = SanitizePlanName(_naming.BuildPlanName(prefs, city, string.Empty, lang))
                       ?? $"Plan · {city}";
        }

        // 9. Materialización (servicio): validación atómica/opaca de places, seed FNV determinista,
        //    scheduling sobre el set fijo + reconcile no-loss, y persistencia atómica plan+stops.
        var result = await _planService.MaterializeAsync(
            userId.Value, city, days, placeIds, importedFromPlatform, creatorHandle, planName, lang, ct);
        if (result.Outcome == ImportPlanOutcome.InvalidPlaces)
            return BadRequest(new { error = "import_invalid_places" });

        // 10. Respuesta: el PlanDetailDto del plan creado (como el flujo normal de creación) para que
        //     la app navegue directa al plan. Recargamos con stops + places para agrupar por días.
        var created = await _db.Plans.AsNoTracking()
            .Include(p => p.Stops)
            .ThenInclude(s => s.Place)
            .FirstAsync(p => p.Id == result.PlanId, ct);

        return Created($"/plans/{result.PlanId}",
            PlanDetailDto.FromEntity(created, lang, null, _config["Api:PublicBaseUrl"]));
    }

    // ── Sanitización / helpers ───────────────────────────────────────────────────

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
}
