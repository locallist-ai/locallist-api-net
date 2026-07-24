using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LocalList.API.NET.Features.Plans;
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

    public FollowController(LocalListDbContext db, TimeProvider clock, ILogger<FollowController> logger, PostHogService posthog, IConfiguration config)
    {
        _db = db;
        _clock = clock;
        _logger = logger;
        _posthog = posthog;
        _config = config;
    }

    /// <summary>Creates a new follow session (state: active). Rejects if user already has an active session. Requires auth.</summary>
    [HttpPost("start")]
    public async Task<IActionResult> StartSession([FromBody] FollowStartRequest request, CancellationToken ct)
    {
        var userId = await GetUserIdAsync(ct);
        if (userId == null) return Unauthorized(new { error = "Invalid token claims" });

        // IDOR guard: only public (curated) plans or the caller's own plans may be followed.
        // Without this, a user with the GUID of someone else's private plan could start a
        // session and read its itinerary via GetActiveSession. Same 404 pattern as
        // PlansController.GetPlan so existence of private plans is not leaked.
        var plan = await _db.Plans.AsNoTracking()
            .Where(p => p.Id == request.PlanId)
            .Select(p => new { p.IsPublic, p.CreatedById })
            .FirstOrDefaultAsync(ct);

        if (plan == null || (!plan.IsPublic && plan.CreatedById != userId))
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
        var updated = await AdvanceSessionInternal(id, ct);
        if (updated == null) return NotFound(new { error = "Session not found or not active" });
        _logger.LogInformation("Follow session {SessionId}: {Action}", id, "next");
        return Ok(updated);
    }

    /// <summary>Skips the current stop (same advancement logic as /next, semantically different for analytics).</summary>
    [HttpPatch("{id:guid}/skip")]
    public async Task<IActionResult> SkipStop(Guid id, CancellationToken ct)
    {
        var updated = await AdvanceSessionInternal(id, ct);
        if (updated == null) return NotFound(new { error = "Session not found or not active" });
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

    private async Task<FollowSession?> GetSessionForUpdate(Guid sessionId, CancellationToken ct)
    {
        var userId = await GetUserIdAsync(ct);
        if (userId == null) return null;

        return await _db.FollowSessions
            .Where(fs => fs.Id == sessionId && fs.UserId == userId.Value)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<FollowSession?> AdvanceSessionInternal(Guid sessionId, CancellationToken ct)
    {
        var userId = await GetUserIdAsync(ct);
        if (userId == null) return null;

        var session = await _db.FollowSessions
            .Where(fs => fs.Id == sessionId && fs.UserId == userId.Value && fs.Status == "active")
            .FirstOrDefaultAsync(ct);

        if (session == null) return null;

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
                _logger.LogInformation("Follow session {SessionId}: auto-completed (end of plan)", sessionId);
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
