using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using LocalList.API.NET.Data;

namespace LocalList.API.NET.Controllers;

[ApiController]
[Route("plans")]
public class PlansController : ControllerBase
{
    private readonly LocalListDbContext _db;

    public PlansController(LocalListDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    [AllowAnonymous] // Mimicking optional auth
    public async Task<IActionResult> GetPlans(
        [FromQuery] string? city,
        [FromQuery] string? type,
        [FromQuery] bool showcase = false,
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0)
    {
        var isAuthenticated = User.Identity?.IsAuthenticated ?? false;

        var query = _db.Plans.Where(p => p.IsPublic);

        if (!string.IsNullOrEmpty(city))
            query = query.Where(p => p.City == city);

        if (!string.IsNullOrEmpty(type))
            query = query.Where(p => p.Type == type);

        // Unauthenticated users only see showcase plans
        if (!isAuthenticated || showcase)
            query = query.Where(p => p.IsShowcase);

        var plans = await query
            .OrderBy(p => p.CreatedAt)
            .Skip(offset)
            .Take(limit)
            .ToListAsync();

        return Ok(new
        {
            plans,
            total = plans.Count
        });
    }

    [HttpGet("{id}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetPlan(Guid id)
    {
        var plan = await _db.Plans
            .Include(p => p.Stops)
            .ThenInclude(s => s.Place)
            .FirstOrDefaultAsync(p => p.Id == id);

        if (plan == null)
            return NotFound(new { error = "Plan not found" });

        var userIdString = User.FindFirstValue(ClaimTypes.NameIdentifier);
        Guid? userId = string.IsNullOrEmpty(userIdString) ? null : Guid.Parse(userIdString);

        if (!plan.IsPublic && plan.CreatedById != userId)
            return NotFound(new { error = "Plan not found" });

        // Group stops by day to match the legacy format
        var days = plan.Stops
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
            plan.Id,
            plan.Name,
            plan.City,
            plan.Type,
            plan.Description,
            plan.ImageUrl,
            plan.DurationDays,
            plan.TripContext,
            plan.IsPublic,
            plan.IsShowcase,
            plan.CreatedById,
            plan.CreatedAt,
            plan.UpdatedAt,
            days
        });
    }
}
