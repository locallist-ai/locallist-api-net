using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LocalList.API.NET.Features.Plans;
using LocalList.API.NET.Shared.Access;
using LocalList.API.NET.Shared.Auth;
using LocalList.API.NET.Shared.Data;
using LocalList.API.NET.Shared.Data.Entities;
using LocalList.API.NET.Shared.Dtos;
using LocalList.API.NET.Shared.I18n;
using LocalList.API.NET.Shared.PostHog;

namespace LocalList.API.NET.Features.Follow;

[ApiController]
[Route("follow")]
[Authorize]
public class FollowController : ControllerBase
{
    private readonly LocalListDbContext _db;
    private readonly TimeProvider _clock;
    private readonly ILogger<FollowController> _logger;
    private readonly PostHogService _posthog;
    private readonly IConfiguration _config;
    private readonly IPlanAccessService _access;

    public FollowController(LocalListDbContext db, TimeProvider clock, ILogger<FollowController> logger, PostHogService posthog, IConfiguration config, IPlanAccessService access)
    {
        _db = db;
        _clock = clock;
        _logger = logger;
        _posthog = posthog;
        _config = config;
        _access = access;
    }

    /// <summary>Creates a new follow session (state: active). Rejects if user already has an active session. Requires auth.</summary>
    [HttpPost("start")]
    public async Task<IActionResult> StartSession([FromBody] FollowStartRequest request, CancellationToken ct)
    {
        var userId = await GetUserIdAsync(ct);
        if (userId == null) return Unauthorized(new { error = "Invalid token claims" });

        // IDOR guard (#116): solo planes que el caller puede VER (publicos, propios o compartidos
        // como colaborador) pueden seguirse. Sin esto, un user con el GUID de un plan privado ajeno
        // podia iniciar sesion y leer su itinerario via GetActiveSession. La autorizacion vive ahora
        // en IPlanAccessService (mismo 404 no-filtrante que PlansController.GetPlan). CanView es
        // false tanto si el plan no existe como si no es accesible: un unico check cierra el IDOR.
        var access = await _access.GetAccessAsync(request.PlanId, userId, ct);
        if (!access.CanView)
        {
            _logger.LogWarning("User {UserId} attempted to follow inaccessible plan {PlanId}", userId, request.PlanId);
            return NotFound(new { error = "Plan not found" });
        }

        var existing = await _db.FollowSessions.AsNoTracking()
            .Where(fs => fs.UserId == userId.Value && fs.Status == "active")
            .FirstOrDefaultAsync(ct);

        if (existing != null)
            return Conflict(new { error = "You already have an active follow session", sessionId = existing.Id });

        var session = new FollowSession
        {
            UserId = userId.Value,
            PlanId = request.PlanId,
            Status = "active",
            CurrentDayIndex = 1,
            CurrentStopIndex = 0
        };

        _db.FollowSessions.Add(session);
        await _db.SaveChangesAsync(ct);

        await _db.PlanMetrics
            .Where(m => m.PlanId == request.PlanId && !m.WasFollowed)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.WasFollowed, true)
                .SetProperty(m => m.FollowedAt, DateTimeOffset.UtcNow), ct);

        _logger.LogInformation("Follow session started: {SessionId} for plan {PlanId} by user {UserId}", session.Id, request.PlanId, userId.Value);

        _ = _posthog.CaptureAsync(userId.Value.ToString(), "follow_started", new()
        {
            ["plan_id"] = request.PlanId.ToString(),
            ["session_id"] = session.Id.ToString(),
        });

        return CreatedAtAction(nameof(GetActiveSession), new { }, session);
    }

    /// <summary>Returns the user's active follow session with current/next stop details and progress. Returns null session if none active.</summary>
    [HttpGet("active")]
    public async Task<IActionResult> GetActiveSession(CancellationToken ct)
    {
        var userId = await GetUserIdAsync(ct);
        if (userId == null) return Unauthorized(new { error = "Invalid token claims" });

        var session = await _db.FollowSessions.AsNoTracking()
            .Where(fs => fs.UserId == userId.Value && fs.Status == "active")
            .FirstOrDefaultAsync(ct);

        if (session == null)
            return Ok(new { session = (object?)null });

        // Social S1 (MINOR b): re-chequea el acceso al plan EN CADA read. El follow se inició con
        // CanView (IDOR #116), pero el estado pudo cambiar después: el plan pasó a private, el
        // owner lo borró/despublicó, o el owner bloqueó al follower. Cualquiera de esas revoca el
        // follow en curso — no seguimos sirviendo el itinerario. 403 estructurado (la sesión existe
        // y es del user; lo revocado es el acceso al plan subyacente), no 404: el follower ya sabía
        // que el plan existía, no filtramos existencia nueva.
        if (!(await _access.GetAccessAsync(session.PlanId, userId.Value, ct)).CanView)
        {
            _logger.LogInformation("Follow session {SessionId}: plan {PlanId} access revoked mid-follow for user {UserId}", session.Id, session.PlanId, userId);
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "plan_access_revoked" });
        }

        var currentDayStops = await _db.PlanStops.AsNoTracking()
            .Include(ps => ps.Place)
            .Where(ps => ps.PlanId == session.PlanId && ps.DayNumber == session.CurrentDayIndex)
            .OrderBy(ps => ps.OrderIndex)
            .ToListAsync(ct);

        var currentStop = session.CurrentStopIndex < currentDayStops.Count ? currentDayStops[session.CurrentStopIndex] : null;
        var nextStop = session.CurrentStopIndex + 1 < currentDayStops.Count ? currentDayStops[session.CurrentStopIndex + 1] : null;

        // Nunca serializar la entidad Place/PlanStop cruda: expondria la key de Google en
        // Photos (URL places.googleapis.com) y sobre-expondria campos internos de curacion
        // (Flags, AiVibeScore, SubmittedById, ReviewedById, RejectionReason, Embedding...).
        // PlanStopResponseDto embebe PlaceDto (fotos sintetizadas por el proxy + sin campos
        // internos), y PlaceDto replica el place al nivel superior para no romper el contrato.
        var lang = LanguageAccessor.ResolveRequestLanguage(Request);
        var publicBaseUrl = _config["Api:PublicBaseUrl"];

        return Ok(new
        {
            session,
            currentStop = currentStop != null
                ? new
                {
                    stop = PlanStopResponseDto.FromEntity(currentStop, lang, publicBaseUrl),
                    place = currentStop.Place is null ? null : PlaceDto.FromEntity(currentStop.Place, lang, publicBaseUrl)
                }
                : null,
            nextStop = nextStop != null
                ? new
                {
                    stop = PlanStopResponseDto.FromEntity(nextStop, lang, publicBaseUrl),
                    place = nextStop.Place is null ? null : PlaceDto.FromEntity(nextStop.Place, lang, publicBaseUrl)
                }
                : null,
            totalStopsToday = currentDayStops.Count,
            progress = new
            {
                currentDay = session.CurrentDayIndex,
                currentStopInDay = session.CurrentStopIndex,
                totalStopsToday = currentDayStops.Count
            }
        });
    }

    /// <summary>Advances to the next stop. Transitions: active -> active (increments stop/day index). Auto-advances to next day when current day's stops are exhausted.</summary>
    [HttpPatch("{id:guid}/next")]
    public async Task<IActionResult> AdvanceToNextStop(Guid id, CancellationToken ct)
    {
        var (session, revoked) = await LoadActiveWithAccessAsync(id, ct);
        if (session == null) return NotFound(new { error = "Session not found or not active" });
        if (revoked) return StatusCode(StatusCodes.Status403Forbidden, new { error = "plan_access_revoked" });
        var updated = await AdvanceSessionInternal(session, ct);
        _logger.LogInformation("Follow session {SessionId}: {Action}", id, "next");
        return Ok(updated);
    }

    /// <summary>Skips the current stop (same advancement logic as /next, semantically different for analytics).</summary>
    [HttpPatch("{id:guid}/skip")]
    public async Task<IActionResult> SkipStop(Guid id, CancellationToken ct)
    {
        var (session, revoked) = await LoadActiveWithAccessAsync(id, ct);
        if (session == null) return NotFound(new { error = "Session not found or not active" });
        if (revoked) return StatusCode(StatusCodes.Status403Forbidden, new { error = "plan_access_revoked" });
        var updated = await AdvanceSessionInternal(session, ct);
        _logger.LogInformation("Follow session {SessionId}: {Action}", id, "skip");
        return Ok(updated);
    }

    /// <summary>Transitions session: active -> paused. Preserves current position for later resume.</summary>
    [HttpPatch("{id:guid}/pause")]
    public async Task<IActionResult> PauseSession(Guid id, CancellationToken ct)
    {
        var session = await GetSessionForUpdate(id, ct);
        if (session == null) return NotFound(new { error = "Session not found" });

        if (session.Status != "active")
            return BadRequest(new { error = $"Cannot pause a {session.Status} session" });

        session.Status = "paused";
        session.LastActiveAt = _clock.GetUtcNow();

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Follow session {SessionId}: {Action}", id, "pause");
        return Ok(session);
    }

    /// <summary>Transitions session: active/paused -> completed. Terminal state; sets CompletedAt timestamp.</summary>
    [HttpPatch("{id:guid}/complete")]
    public async Task<IActionResult> CompleteSession(Guid id, CancellationToken ct)
    {
        var session = await GetSessionForUpdate(id, ct);
        if (session == null) return NotFound(new { error = "Session not found" });

        if (session.Status != "active" && session.Status != "paused")
            return BadRequest(new { error = $"Cannot complete a {session.Status} session" });

        session.Status = "completed";
        session.CompletedAt = _clock.GetUtcNow();
        session.LastActiveAt = _clock.GetUtcNow();

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Follow session {SessionId}: {Action}", id, "complete");

        var uid = await GetUserIdAsync(ct);
        if (uid.HasValue)
        {
            _ = _posthog.CaptureAsync(uid.Value.ToString(), "follow_completed", new()
            {
                ["plan_id"] = session.PlanId.ToString(),
                ["session_id"] = id.ToString(),
            });
        }

        return Ok(session);
    }

    private async Task<Guid?> GetUserIdAsync(CancellationToken ct)
    {
        return await User.GetUserIdAsync(_db, ct);
    }

    // Nota S1 (MINOR b): pause/complete NO re-chequean el acceso al plan a propósito. No sirven
    // contenido del plan (solo cambian el estado de la sesión) y un follower SIEMPRE debe poder
    // pausar/terminar/abandonar su propia sesión aunque el plan haya dejado de ser accesible. La
    // fuga de contenido se cierra en los read-paths (GetActiveSession, next, skip).
    private async Task<FollowSession?> GetSessionForUpdate(Guid sessionId, CancellationToken ct)
    {
        var userId = await GetUserIdAsync(ct);
        if (userId == null) return null;

        return await _db.FollowSessions
            .Where(fs => fs.Id == sessionId && fs.UserId == userId.Value)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Social S1 (MINOR b): carga la sesión activa del user y re-chequea el acceso al plan.
    /// Devuelve (null, _) si no hay sesión activa con ese id para el user; (session, true) si el
    /// acceso al plan fue revocado tras iniciar el follow (plan→private, borrado/despublicado,
    /// bloqueo owner↔follower); (session, false) si sigue accesible. La sesión vuelve TRACKED para
    /// que <see cref="AdvanceSessionInternal"/> pueda mutarla.
    /// </summary>
    private async Task<(FollowSession? session, bool accessRevoked)> LoadActiveWithAccessAsync(Guid sessionId, CancellationToken ct)
    {
        var userId = await GetUserIdAsync(ct);
        if (userId == null) return (null, false);

        var session = await _db.FollowSessions
            .Where(fs => fs.Id == sessionId && fs.UserId == userId.Value && fs.Status == "active")
            .FirstOrDefaultAsync(ct);

        if (session == null) return (null, false);

        var revoked = !(await _access.GetAccessAsync(session.PlanId, userId.Value, ct)).CanView;
        return (session, revoked);
    }

    private async Task<FollowSession?> AdvanceSessionInternal(FollowSession session, CancellationToken ct)
    {
        var dayStopsCount = await _db.PlanStops
            .Where(ps => ps.PlanId == session.PlanId && ps.DayNumber == session.CurrentDayIndex)
            .CountAsync(ct);

        int newStopIndex = session.CurrentStopIndex + 1;
        int newDayIndex = session.CurrentDayIndex;

        if (newStopIndex >= dayStopsCount)
        {
            // Check if there are more days
            var nextDayExists = await _db.PlanStops
                .AnyAsync(ps => ps.PlanId == session.PlanId && ps.DayNumber == newDayIndex + 1, ct);

            if (!nextDayExists)
            {
                // End of plan — auto-complete
                session.Status = "completed";
                session.CompletedAt = _clock.GetUtcNow();
                session.LastActiveAt = _clock.GetUtcNow();
                await _db.SaveChangesAsync(ct);
                _logger.LogInformation("Follow session {SessionId}: auto-completed (end of plan)", session.Id);
                return session;
            }

            newDayIndex += 1;
            newStopIndex = 0;
        }

        session.CurrentStopIndex = newStopIndex;
        session.CurrentDayIndex = newDayIndex;
        session.LastActiveAt = _clock.GetUtcNow();

        await _db.SaveChangesAsync(ct);
        return session;
    }
}
