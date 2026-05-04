using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LocalList.API.NET.Shared.Auth;
using LocalList.API.NET.Shared.Data;
using LocalList.API.NET.Shared.Data.Entities;
using LocalList.API.NET.Shared.I18n;

namespace LocalList.API.NET.Features.Plans;

[ApiController]
[Route("plans")]
public class PlansController : ControllerBase
{
    private readonly LocalListDbContext _db;
    private readonly ILogger<PlansController> _logger;
    private readonly LanguageAccessor _lang;

    public PlansController(LocalListDbContext db, ILogger<PlansController> logger, LanguageAccessor lang)
    {
        _db = db;
        _logger = logger;
        _lang = lang;
    }

    [HttpPost]
    [Authorize]
    public async Task<IActionResult> CreatePlan([FromBody] CreateUserPlanRequest request, CancellationToken ct)
    {
        var userId = await User.GetUserIdAsync(_db, ct);
        if (userId == null)
            return Unauthorized(new { error = "Invalid token" });

        var now = DateTimeOffset.UtcNow;

        var plan = new Plan
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            City = request.City.Trim(),
            Type = request.Type?.Trim() ?? "custom",
            DurationDays = request.DurationDays,
            IsPublic = false,
            IsShowcase = false,
            CreatedById = userId.Value,
            CreatedAt = now,
            UpdatedAt = now,
        };

        _db.Plans.Add(plan);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("User {UserId} created plan {PlanId} ({Name})", userId, plan.Id, plan.Name);

        return Created($"/plans/{plan.Id}", PlanDetailDto.FromEntityWithAllDays(plan, _lang.Language));
    }

    [HttpGet("mine")]
    [Authorize]
    public async Task<IActionResult> GetMyPlans(CancellationToken ct)
    {
        var userId = await User.GetUserIdAsync(_db, ct);
        if (userId == null)
            return Unauthorized(new { error = "Invalid token" });

        var plans = await _db.Plans.AsNoTracking()
            .Where(p => p.CreatedById == userId.Value)
            .OrderByDescending(p => p.UpdatedAt)
            .Take(50)
            .ToListAsync(ct);

        var lang = _lang.Language;
        return Ok(new PlansListResponse(
            plans.Select(p => PlanDto.FromEntity(p, lang)).ToList(),
            plans.Count
        ));
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

        var lang = _lang.Language;
        return Ok(new PlansListResponse(
            plans.Select(p => PlanDto.FromEntity(p, lang)).ToList(),
            total
        ));
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

        Guid? userId = await User.GetUserIdAsync(_db, ct);

        if (!plan.IsPublic && plan.CreatedById != userId)
        {
            _logger.LogWarning("User {UserId} attempted to access private plan {PlanId}", userId, id);
            return NotFound(new { error = "Plan not found" });
        }

        return Ok(PlanDetailDto.FromEntity(plan, _lang.Language));
    }
}
