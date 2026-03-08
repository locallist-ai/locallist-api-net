using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using LocalList.API.NET.Shared.Data;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.NET.Features.Follow;

[ApiController]
[Route("follow")]
[Authorize]
public class FollowController : ControllerBase
{
    private readonly LocalListDbContext _db;
    private readonly TimeProvider _clock;
    private readonly ILogger<FollowController> _logger;

    public FollowController(LocalListDbContext db, TimeProvider clock, ILogger<FollowController> logger)
    {
        _db = db;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Creates a new follow session (state: active). Rejects if user already has an active session. Requires auth.</summary>
    [HttpPost("start")]
    public async Task<IActionResult> StartSession([FromBody] FollowStartRequest request, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token claims" });

        var existing = await _db.FollowSessions
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

        _logger.LogInformation("Follow session started: {SessionId} for plan {PlanId} by user {UserId}", session.Id, request.PlanId, userId.Value);
        return CreatedAtAction(nameof(GetActiveSession), new { }, session);
    }

    /// <summary>Returns the user's active follow session with current/next stop details and progress. Returns null session if none active.</summary>
    [HttpGet("active")]
    public async Task<IActionResult> GetActiveSession(CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token claims" });

        var session = await _db.FollowSessions
            .Where(fs => fs.UserId == userId.Value && fs.Status == "active")
            .FirstOrDefaultAsync(ct);

        if (session == null)
            return Ok(new { session = (object?)null });

        var stops = await _db.PlanStops
            .Include(ps => ps.Place)
            .Where(ps => ps.PlanId == session.PlanId)
            .OrderBy(ps => ps.DayNumber)
            .ThenBy(ps => ps.OrderIndex)
            .ToListAsync(ct);

        var currentDayStops = stops.Where(s => s.DayNumber == session.CurrentDayIndex).ToList();

        var currentStop = session.CurrentStopIndex < currentDayStops.Count ? currentDayStops[session.CurrentStopIndex] : null;
        var nextStop = session.CurrentStopIndex + 1 < currentDayStops.Count ? currentDayStops[session.CurrentStopIndex + 1] : null;

        return Ok(new
        {
            session,
            currentStop = currentStop != null ? new { stop = currentStop, place = currentStop.Place } : null,
            nextStop = nextStop != null ? new { stop = nextStop, place = nextStop.Place } : null,
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

        session.Status = "completed";
        session.CompletedAt = _clock.GetUtcNow();
        session.LastActiveAt = _clock.GetUtcNow();

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Follow session {SessionId}: {Action}", id, "complete");
        return Ok(session);
    }

    private Guid? GetUserId()
    {
        var idStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return string.IsNullOrEmpty(idStr) ? null : Guid.Parse(idStr);
    }

    private async Task<FollowSession?> GetSessionForUpdate(Guid sessionId, CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId == null) return null;

        return await _db.FollowSessions
            .Where(fs => fs.Id == sessionId && fs.UserId == userId.Value)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<FollowSession?> AdvanceSessionInternal(Guid sessionId, CancellationToken ct)
    {
        var userId = GetUserId();
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
