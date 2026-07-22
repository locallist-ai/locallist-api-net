using Microsoft.EntityFrameworkCore;
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
///   - Planes guardados: free 5 activos (activos = filas en plans con created_by = user;
///     DELETE /plans/:id libera hueco) · Plus ilimitado.
///   - Favoritos: free 50 · Plus ilimitado. HUECO DOCUMENTADO: el backend no tiene modelo de
///     favoritos todavía (solo UserProfile.FavoriteCity, que es otra cosa) — el límite se
///     implementará con el modelo. No se inventa aquí.
///
/// El tier se lee SIEMPRE fresco de DB (patrón RequireProAuthorizationFilter): el claim
/// <c>tier</c> del JWT vive 15 min, se desincroniza tras upgrade/downgrade y es falsificable
/// con el guard equivocado.
///
/// Semántica del contador (elegida, con test): el permiso se consume cuando la generación
/// ARRANCA. Los gates de validación (duración, planes guardados) rechazan ANTES de consumir;
/// a partir de ahí el permiso no se devuelve aunque el pipeline falle (LLM caído, ciudad sin
/// places…). Motivo: el coste (Gemini + RAG) ya se ha pagado, y "devolver si falla" abriría
/// un lateral de retry-abuse barato contra el límite mensual.
///
/// Carrera residual (aceptada y acotada): el check de planes guardados es count-then-insert
/// sin serialización por usuario; N generaciones simultáneas del mismo free user pueden
/// dejarle en 5+N-1 planes. El contador mensual (atómico, máx 3/mes) acota N≤3, y el exceso
/// no es explotable como bypass del catálogo (no genera planes extra, solo huecos de
/// almacenamiento). Los gates de dinero del criterio de aceptación (4º plan del mes, 4+ días,
/// multi-ciudad) NO dependen de este check.
/// </summary>
public class PlanGenerationGateService : IPlanGenerationGateService
{
    public const string TierPro = "pro";

    public const int FreeMaxDays = 3;
    public const int PlusMaxDays = 14;
    public const int FreeMonthlyPlanLimit = 3;
    public const int PlusDailyPlanCap = 50;
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

        // 3. Cupo de planes guardados (free): la generación autenticada SIEMPRE persiste el
        //    plan, así que el endpoint de generación ES el endpoint de guardado.
        if (!isPro)
        {
            var saved = await _db.Plans.CountAsync(p => p.CreatedById == userId, ct);
            if (saved >= FreeSavedPlansLimit)
            {
                _logger.LogInformation(
                    "PlanGate: saved-plans denied userId={UserId} saved={Saved}", userId, saved);
                return PlanGateResult.Reject(tier, maxDays, StatusCodes.Status403Forbidden,
                    new { error = "saved_plans_limit_reached", used = saved, limit = FreeSavedPlansLimit });
            }
        }

        // 4. Contador — último gate: si consume, la generación arranca sí o sí.
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
