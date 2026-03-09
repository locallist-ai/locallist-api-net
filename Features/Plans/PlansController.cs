using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using LocalList.API.NET.Shared.Data;

namespace LocalList.API.NET.Features.Plans;

[ApiController]
[Route("plans")]
public class PlansController : ControllerBase
{
    private readonly LocalListDbContext _db;
    private readonly ILogger<PlansController> _logger;

    public PlansController(LocalListDbContext db, ILogger<PlansController> logger)
    {
        _db = db;
        _logger = logger;
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> GetPlans(
        [FromQuery] string? city,
        [FromQuery] string? type,
        [FromQuery] bool showcase = false,
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 100);
        var isAuthenticated = User.Identity?.IsAuthenticated ?? false;

        var query = _db.Plans.AsNoTracking().Where(p => p.IsPublic);

        if (!string.IsNullOrEmpty(city))
            query = query.Where(p => p.City == city);

        if (!string.IsNullOrEmpty(type))
            query = query.Where(p => p.Type == type);

        // Unauthenticated users only see showcase plans
        if (!isAuthenticated || showcase)
            query = query.Where(p => p.IsShowcase);

        var total = await query.CountAsync(ct);

        var plans = await query
            .OrderBy(p => p.CreatedAt)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

        return Ok(new
        {
            plans,
            total
        });
    }

    [HttpGet("{id}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetPlan(Guid id, CancellationToken ct)
    {
        var plan = await _db.Plans.AsNoTracking()
            .Include(p => p.Stops)
            .ThenInclude(s => s.Place)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

        if (plan == null)
            return NotFound(new { error = "Plan not found" });

        var userIdString = User.FindFirstValue(ClaimTypes.NameIdentifier);
        Guid? userId = string.IsNullOrEmpty(userIdString) ? null : Guid.Parse(userIdString);

        if (!plan.IsPublic && plan.CreatedById != userId)
        {
            _logger.LogWarning("User {UserId} attempted to access private plan {PlanId}", userId, id);
            return NotFound(new { error = "Plan not found" });
        }

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
