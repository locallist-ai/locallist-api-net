using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using LocalList.API.NET.Shared.Access;
using LocalList.API.NET.Shared.Auth;
using LocalList.API.NET.Shared.Data;
using LocalList.API.NET.Shared.I18n;
using LocalList.API.NET.Shared.Routing;

namespace LocalList.API.NET.Features.Plans;

/// <summary>
/// Social S1 — compartir un plan por enlace. Primer pilar sobre el cimiento S0 (visibility +
/// share_token + IPlanAccessService).
///
/// Tres endpoints:
///  · <c>POST /plans/{id}/share</c>  — OWNER genera/recupera el token y sube private→unlisted.
///  · <c>DELETE /plans/{id}/share</c> — OWNER revoca el token (unlisted→private; public sigue public).
///  · <c>GET /plans/shared/{token}</c> — cualquiera (anónimo) resuelve el plan POR TOKEN.
///
/// División de responsabilidades con <see cref="IPlanAccessService"/>: el servicio de acceso
/// resuelve por GUID+userId y JAMÁS resuelve un plan 'unlisted' (regla S0). El enlace compartido
/// es un capability distinto — la posesión del token secreto ES la autorización — así que su
/// resolución vive AQUÍ (lookup por share_token), no en el servicio de acceso. Mutar la
/// visibilidad (share/revoke) sí exige ownership, y eso sí se comprueba vía IPlanAccessService.
/// </summary>
[ApiController]
[Route("plans")]
public sealed class PlanShareController : ControllerBase
{
    private readonly LocalListDbContext _db;
    private readonly ILogger<PlanShareController> _logger;
    private readonly LanguageAccessor _lang;
    private readonly ISegmentResolver _routeResolver;
    private readonly IConfiguration _config;
    private readonly IPlanAccessService _access;

    public PlanShareController(
        LocalListDbContext db, ILogger<PlanShareController> logger, LanguageAccessor lang,
        ISegmentResolver routeResolver, IConfiguration config, IPlanAccessService access)
    {
        _db = db;
        _logger = logger;
        _lang = lang;
        _routeResolver = routeResolver;
        _config = config;
        _access = access;
    }

    /// <summary>
    /// Genera (o recupera, idempotente) el enlace compartido de un plan. SOLO el OWNER: un editor
    /// NO comparte. Si el plan es 'private' lo sube a 'unlisted' (espejo is_public consistente vía
    /// el setter de la entidad). Re-share del mismo plan devuelve SIEMPRE el mismo token y no
    /// vuelve a bajar la visibilidad. 404 opaco (indistinguible de "no existe") si el caller no es
    /// el owner, para no filtrar la existencia de planes ajenos.
    /// </summary>
    [HttpPost("{id:guid}/share")]
    [Authorize(AuthenticationSchemes = AuthSchemes.App)]
    public async Task<IActionResult> Share(Guid id, CancellationToken ct)
    {
        var userId = await User.GetUserIdAsync(_db, ct);
        if (userId == null)
            return Unauthorized(new { error = "Invalid token" });

        var access = await _access.GetAccessAsync(id, userId.Value, ct);
        if (!access.IsOwner)
        {
            if (access.PlanExists)
                _logger.LogWarning("User {UserId} attempted to share plan {PlanId} they do not own", userId, id);
            return NotFound(new { error = "Plan not found" });
        }

        var plan = await _db.Plans.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (plan == null)
            return NotFound(new { error = "Plan not found" });

        var alreadyShared = plan.ShareToken != null;

        // private → unlisted (compartir NO publica; el feed público es otra decisión). public y
        // unlisted se dejan como están: re-compartir es idempotente en la visibilidad.
        if (plan.Visibility == "private")
            plan.Visibility = "unlisted";

        if (plan.ShareToken == null)
        {
            // Genera un token único. Colisión (23505) es astronómicamente improbable con 96 bits,
            // pero reintentamos por robustez ante el índice único parcial de share_token.
            for (var attempt = 0; ; attempt++)
            {
                plan.ShareToken = ShareTokenGenerator.Generate();
                plan.UpdatedAt = DateTimeOffset.UtcNow;
                try
                {
                    await _db.SaveChangesAsync(ct);
                    break;
                }
                catch (DbUpdateException ex) when (PostgresErrorPredicates.IsUniqueViolation(ex) && attempt < 4)
                {
                    // Otro token colisionó: descarta el valor y reintenta con uno nuevo.
                    _db.Entry(plan).Property(p => p.ShareToken).CurrentValue = null;
                    plan.ShareToken = null;
                }
            }
        }
        else
        {
            // Ya tenía token: solo persistimos el posible cambio de visibilidad (idempotente).
            plan.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        _logger.LogInformation(
            "User {UserId} shared plan {PlanId} (visibility={Visibility}, reused={Reused})",
            userId, id, plan.Visibility, alreadyShared);

        return Ok(new ShareResponse(plan.ShareToken!, plan.Visibility));
    }

    /// <summary>
    /// Revoca el enlace compartido: <c>share_token=null</c>. Si el plan estaba 'unlisted' vuelve a
    /// 'private' (solo era visible por el token); si estaba 'public' sigue 'public' (la visibilidad
    /// pública es una decisión aparte, revocar el link no la deshace). SOLO el OWNER. Idempotente:
    /// revocar un plan sin token devuelve 204 igual. 404 opaco si no es owner.
    /// </summary>
    [HttpDelete("{id:guid}/share")]
    [Authorize(AuthenticationSchemes = AuthSchemes.App)]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
    {
        var userId = await User.GetUserIdAsync(_db, ct);
        if (userId == null)
            return Unauthorized(new { error = "Invalid token" });

        var access = await _access.GetAccessAsync(id, userId.Value, ct);
        if (!access.IsOwner)
        {
            if (access.PlanExists)
                _logger.LogWarning("User {UserId} attempted to revoke share for plan {PlanId} they do not own", userId, id);
            return NotFound(new { error = "Plan not found" });
        }

        var plan = await _db.Plans.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (plan == null)
            return NotFound(new { error = "Plan not found" });

        var changed = plan.ShareToken != null || plan.Visibility == "unlisted";
        plan.ShareToken = null;
        // 'unlisted' solo era accesible por el token → sin token, vuelve a 'private'. 'public' se
        // mantiene (revocar el link no despublica). El setter mantiene is_public coherente.
        if (plan.Visibility == "unlisted")
            plan.Visibility = "private";

        if (changed)
        {
            plan.UpdatedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("User {UserId} revoked share for plan {PlanId} (visibility={Visibility})", userId, id, plan.Visibility);
        }

        return NoContent();
    }

    /// <summary>
    /// Resuelve un plan compartido POR TOKEN (nunca por GUID) para lectura de solo-lectura. Anónimo:
    /// las <c>&lt;a&gt;</c>/universal links no adjuntan auth. Devuelve <see cref="PlanDetailDto"/>
    /// (con stops+places) si el plan existe, el token coincide y la visibilidad ∈ {unlisted, public}.
    /// Si el plan volvió a 'private', el token no existe, o está vacío → 404 opaco INDISTINGUIBLE
    /// (mismo cuerpo en los tres casos: un atacante no puede saber si un token existió).
    ///
    /// PRIVACIDAD: este DTO va a anónimos. Se anula <c>CreatedById</c> (identificador interno del
    /// dueño) — un enlace unlisted no debe revelar la identidad del owner a cualquiera que tenga el
    /// link. El resto de campos (nombre, ciudad, stops, atribución de import) ya son contenido del
    /// plan, no del dueño. Rate limit por IP (<c>SharedPlanLimit</c>).
    /// </summary>
    [HttpGet("shared/{token}")]
    [AllowAnonymous]
    [EnableRateLimiting("SharedPlanLimit")]
    public async Task<IActionResult> GetShared(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
            return NotFound(new { error = "Plan not found" });

        var plan = await _db.Plans.AsNoTracking()
            .Include(p => p.Stops)
            .ThenInclude(s => s.Place)
            .FirstOrDefaultAsync(p => p.ShareToken == token, ct);

        // Token inexistente/revocado, o el plan volvió a 'private' (token viejo aún en manos de
        // alguien): 404 opaco idéntico. 'unlisted' y 'public' resuelven; nada más.
        if (plan == null || (plan.Visibility != "unlisted" && plan.Visibility != "public"))
            return NotFound(new { error = "Plan not found" });

        var routeSegments = await _routeResolver.ResolveAsync(plan.Stops, RoutingMode.Walking, ct);
        // Privacidad del DTO anónimo (review S1): se anulan los campos del DUEÑO, no del plan.
        //  - CreatedById: identificador interno del owner.
        //  - TripContext: preferencias personales del owner (dieta, presupuesto, groupType,
        //    exclusiones...) — más sensible aún que el id; quien recibe el enlace no lo necesita.
        //  - StartDate SE QUEDA (decisión hub 2026-07-27): es contenido útil del itinerario para
        //    el receptor del enlace (qué días es el viaje), no un dato del dueño.
        // Con el global WhenWritingNull ambos campos desaparecen del JSON.
        var dto = PlanDetailDto.FromEntity(plan, _lang.Language, routeSegments, _config["Api:PublicBaseUrl"])
            with { CreatedById = null, TripContext = null };

        return Ok(dto);
    }
}

/// <summary>Respuesta de <c>POST /plans/{id}/share</c>: token + visibilidad resultante.</summary>
public record ShareResponse(string ShareToken, string Visibility);
