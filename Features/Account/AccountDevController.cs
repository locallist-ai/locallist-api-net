using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using LocalList.API.NET.Shared.Auth;
using LocalList.API.NET.Shared.Constants;
using LocalList.API.NET.Shared.Data;

namespace LocalList.API.NET.Features.Account;

/// <summary>
/// Utilidades SOLO-DEV/TEST del slice Account, para testear flujos Plus/cuota contra los gates
/// REALES del servidor (el "modo pro" de DevTools de la app es solo cliente y no engaña al backend):
///   • <c>POST /account/dev/tier</c> — flipa el tier de FACTURACIÓN real (`User.Tier`) del caller.
///   • <c>POST /account/dev/reset-quota</c> — borra las cuotas de uso (`usage_counters`) del caller
///     (p.ej. los 3 planes IA/mes free + cuotas de import) → contadores a 0 para seguir testeando.
///
/// MODELO DE SEGURIDAD — AMBOS endpoints comparten EXACTAMENTE los mismos gates (helper
/// <see cref="TryGateAsync"/>), FAIL-CLOSED y OPACOS (acceso denegado → 404, nunca 403: un 403
/// revelaría que el endpoint existe; un usuario normal jamás debe siquiera intuirlo). El gate NO
/// depende ya del entorno (antes había un <c>IsProduction()→404</c> que hacía el endpoint inútil
/// contra Railway prod, donde viven los datos que Pablo testea); lo reemplaza un allowlist de emails
/// EXACTOS. Orden:
///   1) Config <c>Dev:TierOverrideEnabled</c> (default FALSE). Off → 404.
///   2) El email del usuario autenticado (fila de DB resuelta por el <c>User.Id</c> del TOKEN, nunca
///      del body) ∈ <c>Dev:AllowedEmails</c> con match EXACTO (case-insensitive + trim). Allowlist
///      VACÍO (default) o email no listado → 404. Es match EXACTO, no por dominio: un email
///      allowlisteado pertenece a una cuenta interna REAL (tomado en <c>users.email</c>, índice
///      único) y NO hay endpoint de cambio de email sin verificar, así que un atacante no puede
///      apropiárselo registrándolo. Esto sustituye al viejo check de dominio <c>@locallist.ai</c>,
///      que era INERTE (register no verifica buzón: cualquiera podía registrar <c>x@locallist.ai</c>).
///
/// DOBLE fail-closed: flag off O allowlist vacío → 404. En Railway prod se abre explícitamente con
/// <c>Dev__TierOverrideEnabled=true</c> + <c>Dev__AllowedEmails__0=&lt;email exacto&gt;</c>.
///
/// El efecto se aplica SIEMPRE al usuario del token (su propio <c>User.Id</c>), NUNCA a un id del
/// body. Ninguno toca <c>rc_customer_id</c> ni la lógica de RevenueCat: el webhook RC sigue siendo
/// la verdad; estos overrides solo existen para test con el flag + allowlist configurados.
///
/// Pineado a AppScheme (usuario de la app, no admin Firebase); anónimo → 401. Sin policy de
/// rate-limit propia: hereda el limitador global (100/min). Nota de opacidad: un body malformado da
/// 400 (filtro de <c>[ApiController]</c>) antes de los gates, así que la EXISTENCIA de la ruta es
/// detectable — lo garantizado fail-closed es el EFECTO (flip de tier / borrado de cuota), no el
/// ocultamiento total de la ruta.
/// </summary>
[ApiController]
[Route("account/dev")]
[Authorize(AuthenticationSchemes = AuthSchemes.App)]
public class AccountDevController : ControllerBase
{
    private readonly LocalListDbContext _db;
    private readonly DevOptions _devOptions;
    private readonly ILogger<AccountDevController> _logger;

    public AccountDevController(
        LocalListDbContext db,
        IOptions<DevOptions> devOptions,
        ILogger<AccountDevController> logger)
    {
        _db = db;
        _devOptions = devOptions.Value;
        _logger = logger;
    }

    public sealed class DevTierRequest
    {
        public string? Tier { get; set; }
    }

    /// <summary>
    /// Gates compartidos (1 flag + 2 allowlist de email exacto) resolviendo el usuario por el id
    /// del TOKEN, nunca del body. Devuelve <c>(user, null)</c> si pasa todos los gates, o
    /// <c>(null, 404)</c> fail-closed en el primero que falle. Cada rama del fallo es un 404
    /// idéntico → opaco (no distingue "flag off" de "no allowlisteado" de "no existes").
    /// </summary>
    private async Task<(LocalList.API.NET.Shared.Data.Entities.User? User, IActionResult? Failure)> TryGateAsync(CancellationToken ct)
    {
        // ── Gate 1: config flag (fail-closed). Off → 404 opaco.
        if (!_devOptions.TierOverrideEnabled)
            return (null, NotFound());

        // Resolver SIEMPRE el usuario por el id del token (AppScheme: sub == Guid), NUNCA por el body.
        var sub = User.GetFirebaseUid();
        if (string.IsNullOrEmpty(sub) || !Guid.TryParse(sub, out var userId))
            return (null, NotFound());

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user == null)
            return (null, NotFound());

        // ── Gate 2: allowlist de email EXACTO (fuente de verdad = fila de DB). Segundo fail-closed:
        // allowlist vacío (default) O email no listado → 404 opaco. Match EXACTO byte-a-byte
        // (Ordinal, case-sensitive, SIN trim); NO por dominio (el dominio era inerte: register no
        // verifica buzón).
        if (!IsAllowedEmail(user.Email))
            return (null, NotFound());

        return (user, null);
    }

    /// <summary>
    /// True solo si <paramref name="email"/> coincide EXACTAMENTE (<see cref="StringComparison.Ordinal"/>,
    /// case-sensitive, SIN trim) con alguna entrada de <c>Dev:AllowedEmails</c>. La semántica DEBE
    /// espejar la unicidad de <c>users.email</c> (varchar: case-sensitive + whitespace-sensitive):
    /// solo así el email allowlisteado es INAPROPIABLE — ninguna otra cuenta puede tener ese email
    /// EXACTO (lo tiene la cuenta interna real), y una variante de caja/espacios (<c>PABLO@…</c>,
    /// <c> pablo@…</c>) crea una fila DISTINTA que NO matchea → 404. Comparar case-insensitive o con
    /// trim reabriría el bypass (registrar una variante para colarse por el gate). Allowlist vacío →
    /// siempre false.
    /// </summary>
    private bool IsAllowedEmail(string? email)
    {
        if (string.IsNullOrEmpty(email)) return false;
        foreach (var allowed in _devOptions.AllowedEmails)
        {
            if (string.IsNullOrEmpty(allowed)) continue;
            if (string.Equals(allowed, email, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    [HttpPost("tier")]
    public async Task<IActionResult> SetTier([FromBody] DevTierRequest request, CancellationToken ct)
    {
        var (user, failure) = await TryGateAsync(ct);
        if (failure is not null) return failure;

        // ── Gate 3: valor de tier estricto. Otro → 400 (aquí ya es un caller interno legítimo).
        var tier = request?.Tier;
        if (tier is not (Tiers.Pro or Tiers.Free))
            return BadRequest(new { error = "invalid_tier", allowed = new[] { Tiers.Pro, Tiers.Free } });

        user!.Tier = tier;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        _logger.LogWarning(
            "DEV tier override applied: user {UserId} tier set to {Tier} (Dev:TierOverrideEnabled=true)",
            user.Id, tier);

        return Ok(new { tier = user.Tier });
    }

    [HttpPost("reset-quota")]
    public async Task<IActionResult> ResetQuota(CancellationToken ct)
    {
        var (user, failure) = await TryGateAsync(ct);
        if (failure is not null) return failure;

        // Borra TODAS las filas de usage_counters del caller (todas las features + periodos):
        // generación mensual de planes + import mensual/diario → cuotas a 0. Dev-only, así que un
        // DELETE directo por user_id es válido (no hay método de reset en IUsageCounterService).
        // Siempre el id del TOKEN, nunca del body.
        var deleted = await _db.UsageCounters
            .Where(c => c.UserId == user!.Id)
            .ExecuteDeleteAsync(ct);

        _logger.LogWarning(
            "DEV quota reset: deleted {Count} usage_counter rows for user {UserId} (Dev:TierOverrideEnabled=true)",
            deleted, user!.Id);

        return Ok(new { reset = deleted });
    }
}
