using Microsoft.EntityFrameworkCore;
using LocalList.API.NET.Shared.Constants;
using LocalList.API.NET.Shared.Data;

namespace LocalList.API.NET.Shared.Usage;

/// <summary>
/// Catálogo Plus vs free (decisión de producto CERRADA 2026-07-13, no cambiar sin Pablo):
///
///   - Planes IA: free 3/mes (mes natural UTC) · Plus ilimitado con cap antiabuso 50/día (UTC).
///   - Duración: free hasta 3 días · Plus hasta 14 (hard cap para todos).
///   - Multi-ciudad: solo Plus. NOTA ESTRUCTURAL: el modelo de request actual es mono-ciudad
///     (TripContextDto.City / ChatSlots.City son escalares y Plan.City también), así que hoy es
///     imposible POR CONSTRUCCIÓN pedir un plan multi-ciudad — no hay nada que validar. Cuando
///     el modelo gane una lista de ciudades, este gate debe rechazar free con
///     <c>403 {error:"multicity_requires_plus"}</c> (código reservado para la app).
///   - Planes guardados: free 5 activos · Plus ilimitado. IMPORTANTE (decisión Pablo
///     2026-07-22): este límite es un cupo de ALMACENAMIENTO y NO vive aquí. Se aplica en
///     <c>POST /plans</c> (PlansController), INDEPENDIENTE del contador mensual de generación:
///     un free con 5 planes manuales puede seguir generando sus 3 planes IA/mes (y viceversa).
///     Antes vivía en este gate y contaminaba la generación (un free con 5 planes recibía
///     <c>saved_plans_limit_reached</c> al generar aunque tuviera 0/3 del mes).
///   - Favoritos: free 50 · Plus ilimitado. HUECO DOCUMENTADO: el backend no tiene modelo de
///     favoritos todavía (solo UserProfile.FavoriteCity, que es otra cosa) — el límite se
///     implementará con el modelo. No se inventa aquí.
///
/// El tier se lee SIEMPRE fresco de DB (patrón RequireProAuthorizationFilter): el claim
/// <c>tier</c> del JWT vive 15 min, se desincroniza tras upgrade/downgrade y es falsificable
/// con el guard equivocado.
///
/// Semántica del contador (elegida, con test): el permiso se consume cuando la generación
/// ARRANCA. El gate de validación de duración rechaza ANTES de consumir; a partir de ahí el
/// permiso no se devuelve aunque el pipeline falle (LLM caído, ciudad sin places…). Motivo:
/// el coste (Gemini + RAG) ya se ha pagado, y "devolver si falla" abriría un lateral de
/// retry-abuse barato contra el límite mensual.
/// </summary>
public class PlanGenerationGateService : IPlanGenerationGateService
{
    public const string TierPro = "pro";

    public const int FreeMaxDays = 3;

    /// <summary>Plus duration ceiling = global hard cap. Single source of truth in
    /// <see cref="PlanLimits.MaxPlanDurationDays"/> so the DTO <c>[Range]</c> validations and this
    /// gate never desync.</summary>
    public const int PlusMaxDays = PlanLimits.MaxPlanDurationDays;
    public const int FreeMonthlyPlanLimit = 3;
    public const int PlusDailyPlanCap = 50;

    /// <summary>Free saved-plans quota. Enforced in <c>POST /plans</c> (PlansController), NOT in
    /// this generation gate — storage cap independent from the monthly AI counter (see class doc).</summary>
    public const int FreeSavedPlansLimit = 5;

    /// <summary>Feature key del contador mensual free (periodo = primer día del mes UTC).</summary>
    public const string FeatureMonthly = "ai_plans_month";

    /// <summary>
    /// Feature key del cap diario Plus (periodo = día UTC). Key distinta del mensual a
    /// propósito: el día 1 del mes ambos periodos empiezan la misma fecha y compartirían
    /// fila si la key fuera común (un downgrade ese día mezclaría los dos consumos).
    /// </summary>
    public const string FeatureDaily = "ai_plans_day";

    private readonly LocalListDbContext _db;
    private readonly IUsageCounterService _counters;
    private readonly TimeProvider _time;
    private readonly ILogger<PlanGenerationGateService> _logger;

    public PlanGenerationGateService(
        LocalListDbContext db,
        IUsageCounterService counters,
        TimeProvider time,
        ILogger<PlanGenerationGateService> logger)
    {
        _db = db;
        _counters = counters;
        _time = time;
        _logger = logger;
    }

    public async Task<PlanGateResult> CheckAndConsumeAsync(
        Guid userId, int? requestedDays, CancellationToken ct)
    {
        // 1. Tier fresco de DB. Un token válido cuyo user ya no existe no es un 403 de
        //    catálogo sino una identidad muerta → mismo 401 que emite RequirePro.
        var tier = await _db.Users
            .Where(u => u.Id == userId)
            .Select(u => u.Tier)
            .FirstOrDefaultAsync(ct);

        if (tier is null)
            return PlanGateResult.Reject("none", 0, StatusCodes.Status401Unauthorized,
                new { error = "Invalid token claims." });

        var isPro = string.Equals(tier, TierPro, StringComparison.Ordinal);
        var maxDays = isPro ? PlusMaxDays : FreeMaxDays;

        // 2. Duración pedida explícitamente. El hard cap global (14) lo corta también la
        //    validación del DTO (Range) y el clamp del pipeline; esto es defensa en
        //    profundidad con error estructurado.
        if (requestedDays > PlusMaxDays)
            return PlanGateResult.Reject(tier, maxDays, StatusCodes.Status400BadRequest,
                new { error = "duration_invalid", requestedDays, maxDays = PlusMaxDays });

        if (!isPro && requestedDays > FreeMaxDays)
        {
            _logger.LogInformation(
                "PlanGate: duration denied userId={UserId} days={Days}", userId, requestedDays);
            return PlanGateResult.Reject(tier, maxDays, StatusCodes.Status403Forbidden,
                new
                {
                    error = "duration_requires_plus",
                    requestedDays,
                    maxDays = FreeMaxDays,
                    plusMaxDays = PlusMaxDays,
                });
        }

        // 3. Contador — último gate: si consume, la generación arranca sí o sí.
        //    (El cupo de planes guardados NO se comprueba aquí — es un límite de
        //    almacenamiento independiente que vive en POST /plans; ver class doc.)
        var now = _time.GetUtcNow();
        if (!isPro)
        {
            var monthStart = new DateOnly(now.Year, now.Month, 1);
            if (!await _counters.TryConsumeAsync(userId, FeatureMonthly, monthStart, FreeMonthlyPlanLimit, ct))
            {
                var used = await _counters.GetUsedAsync(userId, FeatureMonthly, monthStart, ct);
                var resetsAt = new DateTimeOffset(
                    monthStart.AddMonths(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
                _logger.LogInformation(
                    "PlanGate: monthly limit denied userId={UserId} used={Used}", userId, used);
                return PlanGateResult.Reject(tier, maxDays, StatusCodes.Status403Forbidden,
                    new { error = "plan_limit_reached", used, limit = FreeMonthlyPlanLimit, resetsAt });
            }
        }
        else
        {
            // Cap antiabuso Plus: 429, no 403 — no es una carencia de entitlement (la app no
            // debe pintar upsell a un usuario que YA es Plus) sino throttling puro.
            var dayStart = DateOnly.FromDateTime(now.UtcDateTime);
            if (!await _counters.TryConsumeAsync(userId, FeatureDaily, dayStart, PlusDailyPlanCap, ct))
            {
                var used = await _counters.GetUsedAsync(userId, FeatureDaily, dayStart, ct);
                var resetsAt = new DateTimeOffset(
                    dayStart.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
                _logger.LogWarning(
                    "PlanGate: pro daily cap hit userId={UserId} used={Used}", userId, used);
                return PlanGateResult.Reject(tier, maxDays, StatusCodes.Status429TooManyRequests,
                    new { error = "daily_cap_reached", used, limit = PlusDailyPlanCap, resetsAt });
            }
        }

        return PlanGateResult.Ok(tier, maxDays);
    }
}
