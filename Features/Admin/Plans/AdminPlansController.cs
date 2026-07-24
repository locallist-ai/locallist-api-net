using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using LocalList.API.NET.Shared.Auth;
using LocalList.API.NET.Shared.Data;
using LocalList.API.NET.Shared.AI.Services;

namespace LocalList.API.NET.Features.Admin.Plans;

// AdminPlansController is split across several partial files by responsibility. This split is
// purely structural (same class, same members, same behavior); it does NOT change any logic:
//   • AdminPlansController.cs             — construction + list/detail reads + delete
//   • AdminPlansController.Create.cs      — single + bulk plan creation (place-name resolution)
//   • AdminPlansController.Update.cs      — atomic metadata+stops PATCH + the deprecated PUT /stops
//   • AdminPlansController.Translation.cs — ES translation draft + batch translation
[ApiController]
[Route("admin/plans")]
[AdminAuthorize]
[EnableRateLimiting("AdminLimit")]
public partial class AdminPlansController : ControllerBase
{
    private readonly LocalListDbContext _db;
    private readonly ILogger<AdminPlansController> _logger;
    private readonly TimeProvider _clock;
    private readonly IPlaceTranslatorService _ai;

    public AdminPlansController(LocalListDbContext db, ILogger<AdminPlansController> logger, TimeProvider clock, IPlaceTranslatorService ai)
    {
        _db = db;
        _logger = logger;
        _clock = clock;
        _ai = ai;
    }

    [HttpGet]
    public async Task<IActionResult> GetPlans(
        [FromQuery] string? city,
        [FromQuery] string? type,
        [FromQuery] string? source,
        [FromQuery] bool? isPublic,
        [FromQuery] bool? isShowcase,
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 200);
        var query = _db.Plans.AsNoTracking().AsQueryable();

        if (!string.IsNullOrEmpty(city))
            query = query.Where(p => p.City == city);
        if (!string.IsNullOrEmpty(type))
            query = query.Where(p => p.Type == type);
        if (!string.IsNullOrEmpty(source))
            query = query.Where(p => p.Source == source);
        if (isPublic.HasValue)
            query = query.Where(p => p.IsPublic == isPublic.Value);
        if (isShowcase.HasValue)
            query = query.Where(p => p.IsShowcase == isShowcase.Value);

        var total = await query.CountAsync(ct);
        var plans = await query
            .OrderByDescending(p => p.CreatedAt)
            .Skip(offset).Take(limit)
            .ToListAsync(ct);

        return Ok(new AdminPlansListResponse(
            plans.Select(AdminPlanDto.FromEntity).ToList(),
            total
        ));
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetPlan(Guid id, CancellationToken ct)
    {
        var plan = await _db.Plans.AsNoTracking()
            .Include(p => p.Stops)
            .ThenInclude(s => s.Place)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

        if (plan == null)
            return NotFound(new { error = "Plan not found" });

        return Ok(AdminPlanDetailDto.FromEntity(plan));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeletePlan(Guid id, CancellationToken ct)
    {
        var plan = await _db.Plans.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (plan == null)
            return NotFound(new { error = "Plan not found" });

        _db.Plans.Remove(plan); // Cascade deletes stops
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Admin deleted plan {PlanId}", id);
        return NoContent();
    }
}
