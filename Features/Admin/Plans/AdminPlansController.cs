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
            Source = "curated",
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
                Source = "curated",
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

    [HttpGet("{id}")]
    public async Task<IActionResult> GetPlan(Guid id, CancellationToken ct)
    {
        var plan = await _db.Plans.AsNoTracking()
            .Include(p => p.Stops)
            .ThenInclude(s => s.Place)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

        if (plan == null)
            return NotFound(new { error = "Plan not found" });

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
            plan.IsPublic,
            plan.IsShowcase,
            plan.CreatedById,
            plan.CreatedAt,
            plan.UpdatedAt,
            days
        });
    }

    [HttpPatch("{id}")]
    public async Task<IActionResult> UpdatePlan(Guid id, [FromBody] UpdatePlanRequest request, CancellationToken ct)
    {
        var plan = await _db.Plans.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (plan == null)
            return NotFound(new { error = "Plan not found" });

        if (request.Name != null) plan.Name = request.Name.Trim();
        if (request.City != null) plan.City = request.City.Trim();
        if (request.Type != null) plan.Type = request.Type.Trim();
        if (request.Description != null) plan.Description = request.Description.Trim();
        if (request.ImageUrl != null) plan.ImageUrl = request.ImageUrl.Trim();
        if (request.DurationDays.HasValue) plan.DurationDays = request.DurationDays.Value;
        if (request.IsPublic.HasValue) plan.IsPublic = request.IsPublic.Value;
        if (request.IsShowcase.HasValue) plan.IsShowcase = request.IsShowcase.Value;

        plan.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Admin updated plan {PlanId}", plan.Id);

        return Ok(new
        {
            plan.Id,
            plan.Name,
            plan.City,
            plan.Type,
            plan.Description,
            plan.ImageUrl,
            plan.DurationDays,
            plan.IsPublic,
            plan.IsShowcase,
            plan.CreatedById,
            plan.CreatedAt,
            plan.UpdatedAt
        });
    }

    [HttpPut("{id}/stops")]
    public async Task<IActionResult> UpdateStops(Guid id, [FromBody] UpdatePlanStopsRequest request, CancellationToken ct)
    {
        var plan = await _db.Plans
            .Include(p => p.Stops)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

        if (plan == null)
            return NotFound(new { error = "Plan not found" });

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
        var placeIds = request.Stops
            .Where(s => s.PlaceId.HasValue)
            .Select(s => s.PlaceId!.Value)
            .Distinct()
            .ToList();

        if (placeIds.Count > 0)
        {
            var existingPlaceIds = await _db.Places
                .Where(p => placeIds.Contains(p.Id))
                .Select(p => p.Id)
                .ToListAsync(ct);

            var missingIds = placeIds.Except(existingPlaceIds).ToList();
            if (missingIds.Count > 0)
                return BadRequest(new { error = $"Places not found: {string.Join(", ", missingIds)}" });
        }

        var unresolvedStops = request.Stops
            .Where(s => s.PlaceId == null && (string.IsNullOrEmpty(s.PlaceName) || !nameToId.ContainsKey(s.PlaceName!)))
            .ToList();

        if (unresolvedStops.Count > 0)
        {
            var names = unresolvedStops.Select(s => s.PlaceName ?? "(no name)");
            return BadRequest(new { error = $"Could not resolve places: {string.Join(", ", names)}" });
        }

        // Atomic replace: delete all existing stops, insert new ones
        var now = _clock.GetUtcNow();
        await using var transaction = await _db.Database.BeginTransactionAsync(ct);

        _db.PlanStops.RemoveRange(plan.Stops);

        var newStops = request.Stops.Select(s => new PlanStop
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

        _db.PlanStops.AddRange(newStops);
        plan.UpdatedAt = now;

        await _db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        _logger.LogInformation("Admin updated stops for plan {PlanId} ({StopCount} stops)", id, newStops.Count);

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
            updatedPlan.IsPublic,
            updatedPlan.IsShowcase,
            updatedPlan.CreatedById,
            updatedPlan.CreatedAt,
            updatedPlan.UpdatedAt,
            days
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
