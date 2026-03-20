using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using LocalList.API.NET.Shared.Auth;
using LocalList.API.NET.Shared.Data;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.NET.Features.Admin.Plans;

[ApiController]
[Route("admin/plans")]
[AdminAuthorize]
[EnableRateLimiting("AdminLimit")]
public class AdminPlansController : ControllerBase
{
    private readonly LocalListDbContext _db;
    private readonly ILogger<AdminPlansController> _logger;
    private readonly TimeProvider _clock;

    public AdminPlansController(LocalListDbContext db, ILogger<AdminPlansController> logger, TimeProvider clock)
    {
        _db = db;
        _logger = logger;
        _clock = clock;
    }

    [HttpGet]
    public async Task<IActionResult> GetPlans(
        [FromQuery] string? city,
        [FromQuery] string? type,
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

        var total = await query.CountAsync(ct);
        var plans = await query
            .OrderByDescending(p => p.CreatedAt)
            .Skip(offset).Take(limit)
            .ToListAsync(ct);

        return Ok(new { plans, total });
    }

    [HttpPost]
    public async Task<IActionResult> CreatePlan([FromBody] CreatePlanRequest request, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var userId = await GetUserIdAsync(ct);

        // Resolve place names to IDs
        var placeNames = request.Stops
            .Where(s => s.PlaceId == null && !string.IsNullOrEmpty(s.PlaceName))
            .Select(s => s.PlaceName!)
            .Distinct()
            .ToList();

        var nameToId = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        if (placeNames.Count > 0)
        {
            var places = await _db.Places.AsNoTracking()
                .Where(p => placeNames.Contains(p.Name) && p.Status == "published")
                .Select(p => new { p.Id, p.Name })
                .ToListAsync(ct);

            foreach (var p in places)
                nameToId.TryAdd(p.Name, p.Id);
        }

        // Validate all stops have a resolvable place
        var unresolvedStops = request.Stops
            .Where(s => s.PlaceId == null && (string.IsNullOrEmpty(s.PlaceName) || !nameToId.ContainsKey(s.PlaceName!)))
            .ToList();

        if (unresolvedStops.Count > 0)
        {
            var names = unresolvedStops.Select(s => s.PlaceName ?? "(no name)");
            return BadRequest(new { error = $"Could not resolve places: {string.Join(", ", names)}" });
        }

        var plan = new Plan
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            City = request.City?.Trim() ?? "Miami",
            Type = request.Type.Trim(),
            Description = request.Description?.Trim(),
            ImageUrl = request.ImageUrl?.Trim(),
            DurationDays = request.DurationDays,
            IsPublic = request.IsPublic,
            IsShowcase = request.IsShowcase,
            CreatedById = userId,
            CreatedAt = now,
            UpdatedAt = now
        };

        var stops = request.Stops.Select(s => new PlanStop
        {
            Id = Guid.NewGuid(),
            PlanId = plan.Id,
            PlaceId = s.PlaceId ?? nameToId[s.PlaceName!],
            DayNumber = s.DayNumber,
            OrderIndex = s.OrderIndex,
            TimeBlock = s.TimeBlock?.Trim(),
            SuggestedArrival = s.SuggestedArrival,
            SuggestedDurationMin = s.SuggestedDurationMin,
            CreatedAt = now
        }).ToList();

        _db.Plans.Add(plan);
        _db.PlanStops.AddRange(stops);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Admin created plan {PlanId} ({Name}) with {StopCount} stops",
            plan.Id, plan.Name, stops.Count);

        return CreatedAtAction(nameof(GetPlans), null, new { plan, stops = stops.Count });
    }

    [HttpPost("bulk")]
    public async Task<IActionResult> BulkCreatePlans([FromBody] List<CreatePlanRequest> requests, CancellationToken ct)
    {
        if (requests.Count == 0)
            return BadRequest(new { error = "Empty request list." });

        var now = _clock.GetUtcNow();
        var userId = await GetUserIdAsync(ct);

        // Collect all place names to resolve in one query
        var allPlaceNames = requests
            .SelectMany(r => r.Stops)
            .Where(s => s.PlaceId == null && !string.IsNullOrEmpty(s.PlaceName))
            .Select(s => s.PlaceName!)
            .Distinct()
            .ToList();

        var nameToId = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        if (allPlaceNames.Count > 0)
        {
            var places = await _db.Places.AsNoTracking()
                .Where(p => allPlaceNames.Contains(p.Name) && p.Status == "published")
                .Select(p => new { p.Id, p.Name })
                .ToListAsync(ct);

            foreach (var p in places)
                nameToId.TryAdd(p.Name, p.Id);
        }

        // Check for unresolved names
        var unresolved = allPlaceNames.Where(n => !nameToId.ContainsKey(n)).ToList();
        if (unresolved.Count > 0)
            return BadRequest(new { error = $"Could not resolve places: {string.Join(", ", unresolved)}" });

        var plansToAdd = new List<Plan>();
        var stopsToAdd = new List<PlanStop>();

        foreach (var request in requests)
        {
            var plan = new Plan
            {
                Id = Guid.NewGuid(),
                Name = request.Name.Trim(),
                City = request.City?.Trim() ?? "Miami",
                Type = request.Type.Trim(),
                Description = request.Description?.Trim(),
                ImageUrl = request.ImageUrl?.Trim(),
                DurationDays = request.DurationDays,
                IsPublic = request.IsPublic,
                IsShowcase = request.IsShowcase,
                CreatedById = userId,
                CreatedAt = now,
                UpdatedAt = now
            };

            var stops = request.Stops.Select(s => new PlanStop
            {
                Id = Guid.NewGuid(),
                PlanId = plan.Id,
                PlaceId = s.PlaceId ?? nameToId[s.PlaceName!],
                DayNumber = s.DayNumber,
                OrderIndex = s.OrderIndex,
                TimeBlock = s.TimeBlock?.Trim(),
                SuggestedArrival = s.SuggestedArrival,
                SuggestedDurationMin = s.SuggestedDurationMin,
                CreatedAt = now
            }).ToList();

            plansToAdd.Add(plan);
            stopsToAdd.AddRange(stops);
        }

        _db.Plans.AddRange(plansToAdd);
        _db.PlanStops.AddRange(stopsToAdd);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Bulk created {PlanCount} plans with {StopCount} total stops",
            plansToAdd.Count, stopsToAdd.Count);

        return Ok(new
        {
            created = plansToAdd.Count,
            totalStops = stopsToAdd.Count,
            plans = plansToAdd.Select(p => new { p.Id, p.Name })
        });
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

    private async Task<Guid?> GetUserIdAsync(CancellationToken ct = default)
    {
        return await User.GetUserIdAsync(_db, ct);
    }
}
