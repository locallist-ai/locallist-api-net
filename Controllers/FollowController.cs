using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using LocalList.API.NET.Data;
using LocalList.API.NET.Data.Models;

namespace LocalList.API.NET.Controllers;

[ApiController]
[Route("follow")]
[Authorize]
public class FollowController : ControllerBase
{
    private readonly LocalListDbContext _db;

    public FollowController(LocalListDbContext db)
    {
        _db = db;
    }

    [HttpPost("start")]
    public async Task<IActionResult> StartSession([FromBody] FollowStartRequest request)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token claims" });

        var existing = await _db.FollowSessions
            .Where(fs => fs.UserId == userId.Value && fs.Status == "active")
            .FirstOrDefaultAsync();

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
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetActiveSession), new { }, session);
    }

    [HttpGet("active")]
    public async Task<IActionResult> GetActiveSession()
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token claims" });

        var session = await _db.FollowSessions
            .Where(fs => fs.UserId == userId.Value && fs.Status == "active")
            .FirstOrDefaultAsync();

        if (session == null)
            return Ok(new { session = (object?)null });

        var stops = await _db.PlanStops
            .Include(ps => ps.Place)
            .Where(ps => ps.PlanId == session.PlanId)
            .OrderBy(ps => ps.DayNumber)
            .ThenBy(ps => ps.OrderIndex)
            .ToListAsync();

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

    [HttpPatch("{id:guid}/next")]
    public async Task<IActionResult> AdvanceToNextStop(Guid id)
    {
        var updated = await AdvanceSessionInternal(id);
        if (updated == null) return NotFound(new { error = "Session not found or not active" });
        return Ok(updated);
    }

    [HttpPatch("{id:guid}/skip")]
    public async Task<IActionResult> SkipStop(Guid id)
    {
        var updated = await AdvanceSessionInternal(id);
        if (updated == null) return NotFound(new { error = "Session not found or not active" });
        return Ok(updated);
    }

    [HttpPatch("{id:guid}/pause")]
    public async Task<IActionResult> PauseSession(Guid id)
    {
        var session = await GetSessionForUpdate(id);
        if (session == null) return NotFound(new { error = "Session not found" });

        session.Status = "paused";
        session.LastActiveAt = DateTimeOffset.UtcNow;
        
        await _db.SaveChangesAsync();
        return Ok(session);
    }

    [HttpPatch("{id:guid}/complete")]
    public async Task<IActionResult> CompleteSession(Guid id)
    {
        var session = await GetSessionForUpdate(id);
        if (session == null) return NotFound(new { error = "Session not found" });

        session.Status = "completed";
        session.CompletedAt = DateTimeOffset.UtcNow;
        session.LastActiveAt = DateTimeOffset.UtcNow;
        
        await _db.SaveChangesAsync();
        return Ok(session);
    }

    private Guid? GetUserId()
    {
        var idStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return string.IsNullOrEmpty(idStr) ? null : Guid.Parse(idStr);
    }

    private async Task<FollowSession?> GetSessionForUpdate(Guid sessionId)
    {
        var userId = GetUserId();
        if (userId == null) return null;

        return await _db.FollowSessions
            .Where(fs => fs.Id == sessionId && fs.UserId == userId.Value)
            .FirstOrDefaultAsync();
    }

    private async Task<FollowSession?> AdvanceSessionInternal(Guid sessionId)
    {
        var userId = GetUserId();
        if (userId == null) return null;

        var session = await _db.FollowSessions
            .Where(fs => fs.Id == sessionId && fs.UserId == userId.Value && fs.Status == "active")
            .FirstOrDefaultAsync();

        if (session == null) return null;

        var dayStopsCount = await _db.PlanStops
            .Where(ps => ps.PlanId == session.PlanId && ps.DayNumber == session.CurrentDayIndex)
            .CountAsync();

        int newStopIndex = session.CurrentStopIndex + 1;
        int newDayIndex = session.CurrentDayIndex;

        if (newStopIndex >= dayStopsCount)
        {
            newDayIndex += 1;
            newStopIndex = 0;
        }

        session.CurrentStopIndex = newStopIndex;
        session.CurrentDayIndex = newDayIndex;
        session.LastActiveAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync();
        return session;
    }
}

public class FollowStartRequest
{
    public Guid PlanId { get; set; }
}
