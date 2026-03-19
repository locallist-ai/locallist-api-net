using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using LocalList.API.NET.Shared.Data;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.NET.Features.Plans;

[ApiController]
[Route("plans")]
[Authorize]
public class PlanEditController : ControllerBase
{
    private readonly LocalListDbContext _db;
    private readonly ILogger<PlanEditController> _logger;

    public PlanEditController(LocalListDbContext db, ILogger<PlanEditController> logger)
    {
        _db = db;
        _logger = logger;
    }

    [HttpPut("{id}/stops")]
    public async Task<IActionResult> UpdateStops(Guid id, [FromBody] UpdateStopsRequest request, CancellationToken ct)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return Unauthorized(new { error = "Invalid token" });

        var userGuid = Guid.Parse(userId);

        var plan = await _db.Plans
            .Include(p => p.Stops)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

        if (plan == null)
            return NotFound(new { error = "Plan not found" });

        if (plan.CreatedById != userGuid)
        {
            _logger.LogWarning("User {UserId} attempted to edit plan {PlanId} owned by {OwnerId}", userId, id, plan.CreatedById);
            return Forbid();
        }

        // Validate all placeIds exist
        var placeIds = request.Stops.Select(s => s.PlaceId).Distinct().ToList();
        var existingPlaceIds = await _db.Places
            .Where(p => placeIds.Contains(p.Id))
            .Select(p => p.Id)
            .ToListAsync(ct);

        var missingIds = placeIds.Except(existingPlaceIds).ToList();
        if (missingIds.Count > 0)
            return BadRequest(new { error = $"Places not found: {string.Join(", ", missingIds)}" });

        // Atomic replace: delete all existing stops, insert new ones
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        _db.PlanStops.RemoveRange(plan.Stops);

        var newStops = request.Stops.Select(s => new PlanStop
        {
            Id = Guid.NewGuid(),
            PlanId = plan.Id,
            PlaceId = s.PlaceId,
            DayNumber = s.DayNumber,
            OrderIndex = s.OrderIndex,
            TimeBlock = s.TimeBlock,
            SuggestedDurationMin = s.SuggestedDurationMin,
            CreatedAt = DateTimeOffset.UtcNow,
        }).ToList();

        _db.PlanStops.AddRange(newStops);
        plan.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        _logger.LogInformation("User {UserId} updated stops for plan {PlanId} ({StopCount} stops)", userId, id, newStops.Count);

        // Re-fetch with Place includes for the response
        var updatedPlan = await _db.Plans.AsNoTracking()
            .Include(p => p.Stops)
            .ThenInclude(s => s.Place)
            .FirstAsync(p => p.Id == id, ct);

        var days = updatedPlan.Stops
            .OrderBy(s => s.DayNumber)
            .ThenBy(s => s.OrderIndex)
            .GroupBy(s => s.DayNumber)
            .Select(g => new
            {
                dayNumber = g.Key,
                stops = g.Select(s => new
                {
                    id = s.Id,
                    orderIndex = s.OrderIndex,
                    timeBlock = s.TimeBlock,
                    suggestedArrival = s.SuggestedArrival,
                    suggestedDurationMin = s.SuggestedDurationMin,
                    travelFromPrevious = s.TravelFromPrevious,
                    place = s.Place
                })
            });

        return Ok(new
        {
            updatedPlan.Id,
            updatedPlan.Name,
            updatedPlan.City,
            updatedPlan.Type,
            updatedPlan.Description,
            updatedPlan.ImageUrl,
            updatedPlan.DurationDays,
            updatedPlan.TripContext,
            updatedPlan.IsPublic,
            updatedPlan.IsShowcase,
            updatedPlan.CreatedById,
            updatedPlan.CreatedAt,
            updatedPlan.UpdatedAt,
            days
        });
    }
}
