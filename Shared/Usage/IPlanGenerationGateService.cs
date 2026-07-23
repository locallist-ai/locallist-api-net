namespace LocalList.API.NET.Shared.Usage;

/// <summary>
/// Gate server-side del catálogo Plus para la generación de planes IA
/// (<c>POST /chat/generate</c> y <c>POST /builder/chat</c>). Valida tier (SIEMPRE fresco de
/// DB, nunca el claim del JWT) y duración pedida, y por último consume el contador de
/// generación (3/mes free · cap antiabuso 50/día Plus). El cupo de planes guardados (5 free)
/// NO vive aquí: es un límite de almacenamiento independiente aplicado en <c>POST /plans</c>.
/// </summary>
public interface IPlanGenerationGateService
{
    /// <summary>
    /// Ejecuta los checks en orden (tier → duración → contador) y consume 1 permiso del
    /// contador SOLO si todos pasan. Un rechazo de un gate previo NO consume contador; una
    /// vez consumido, la generación "ha arrancado" y el permiso no se devuelve aunque el LLM
    /// falle después (semántica elegida, ver README de Billing).
    /// </summary>
    /// <param name="userId">Usuario ya autenticado (los endpoints exigen [Authorize]).</param>
    /// <param name="requestedDays">Días pedidos explícitamente en el request (TripContext.Days
    /// o slot days); null si no vienen. Los días derivados por el LLM del texto libre se
    /// acotan aparte con <see cref="PlanGateResult.MaxDays"/> dentro del pipeline.</param>
    Task<PlanGateResult> CheckAndConsumeAsync(Guid userId, int? requestedDays, CancellationToken ct);
}

/// <summary>Resultado del gate. Si <see cref="Allowed"/> es false, <see cref="Rejection"/> trae el status + body estructurado.</summary>
public sealed record PlanGateResult(bool Allowed, string Tier, int MaxDays, PlanGateRejection? Rejection)
{
    public static PlanGateResult Ok(string tier, int maxDays) => new(true, tier, maxDays, null);
    public static PlanGateResult Reject(string tier, int maxDays, int statusCode, object body) =>
        new(false, tier, maxDays, new PlanGateRejection(statusCode, body));
}

/// <summary>Rechazo estructurado: la app usa <c>error</c> del body para pintar el upsell.</summary>
public sealed record PlanGateRejection(int StatusCode, object Body);
