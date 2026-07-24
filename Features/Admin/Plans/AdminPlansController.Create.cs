using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LocalList.API.NET.Shared.Auth;
using LocalList.API.NET.Shared.Constants;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.NET.Features.Admin.Plans;

// Single + bulk plan creation (place-name → id resolution, max-stops-per-day validation).
// Logic is identical to the original single-file version; only its location changed.
public partial class AdminPlansController
{
    [HttpPost]
    public async Task<IActionResult> CreatePlan([FromBody] CreatePlanRequest request, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var userId = await User.GetUserIdAsync(_db, ct);

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

        // Validate max stops per day
        foreach (var day in request.Stops.GroupBy(s => s.DayNumber))
        {
            if (day.Count() > PlanLimits.MaxStopsPerDay)
                return BadRequest(new
                {
                    error = $"too_many_stops_day_{day.Key}",
                    message = $"Maximum {PlanLimits.MaxStopsPerDay} stops per day (day {day.Key} has {day.Count()})."
                });
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

        var created = await _db.Plans.AsNoTracking()
            .Include(p => p.Stops)
            .ThenInclude(s => s.Place)
            .FirstAsync(p => p.Id == plan.Id, ct);

        return CreatedAtAction(nameof(GetPlans), null, AdminPlanDetailDto.FromEntity(created));
    }

    [HttpPost("bulk")]
    public async Task<IActionResult> BulkCreatePlans([FromBody] List<CreatePlanRequest> requests, CancellationToken ct)
    {
        if (requests.Count == 0)
            return BadRequest(new { error = "Empty request list." });

        var now = _clock.GetUtcNow();
        var userId = await User.GetUserIdAsync(_db, ct);

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

        return Ok(new AdminBulkCreateResultDto(
            plansToAdd.Count,
            stopsToAdd.Count,
            plansToAdd.Select(p => new AdminPlanCreatedDto(p.Id, p.Name)).ToList()
        ));
    }
}
