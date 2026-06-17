using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using LocalList.API.NET.Shared.Auth;
using LocalList.API.NET.Shared.Constants;
using LocalList.API.NET.Shared.Data;
using LocalList.API.NET.Shared.Data.Entities;
using LocalList.API.NET.Shared.AI.Services;
using LocalList.API.NET.Shared.I18n;

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

    /// <summary>
    /// Updates a plan. With <c>stops</c> omitted this is a metadata-only PATCH.
    /// With <c>stops</c> present it ALSO replaces the plan's stops, writing metadata
    /// and stops in ONE EF transaction (a single SaveChanges) — the atomic replacement
    /// for the old PATCH-metadata-then-PUT-/stops flow, which could leave the plan with
    /// new metadata but stale stops if the second call failed.
    /// </summary>
    [HttpPatch("{id}")]
    public async Task<IActionResult> UpdatePlan(Guid id, [FromBody] UpdatePlanRequest request, CancellationToken ct)
    {
        var plan = await _db.Plans
            .Include(p => p.Stops)
            .FirstOrDefaultAsync(p => p.Id == id, ct);
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

        // i18n ES fields
        if (request.NameEs != null)
            plan.NameI18n = LanguageAccessor.SetI18nString(plan.NameI18n, "es", request.NameEs);
        if (request.DescriptionEs != null)
            plan.DescriptionI18n = LanguageAccessor.SetI18nString(plan.DescriptionI18n, "es", request.DescriptionEs);
        if (request.TranslationStatusEs != null)
            plan.TranslationStatus = LanguageAccessor.SetI18nString(plan.TranslationStatus, "es", request.TranslationStatusEs);

        var now = _clock.GetUtcNow();
        plan.UpdatedAt = now;

        // Atomic meta + stops: when `stops` is provided, stage the full stop replacement
        // alongside the metadata changes. A single SaveChanges wraps the metadata UPDATE,
        // the stop DELETEs and the stop INSERTs in one DB transaction. If any of it fails
        // (e.g. a stop references a non-existent place → FK violation), the whole write
        // rolls back, so the plan never ends up with new metadata but stale stops.
        var stopsReplaced = false;
        if (request.Stops != null)
        {
            var (newStops, error) = await BuildStopsForReplaceAsync(plan.Id, request.Stops, now, ct);
            if (error != null) return error;

            _db.PlanStops.RemoveRange(plan.Stops);
            _db.PlanStops.AddRange(newStops!);
            stopsReplaced = true;
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Place-FK or other constraint violation. The implicit transaction around
            // SaveChanges already rolled back — metadata and stops are both unchanged.
            // Surface a 400 instead of letting it bubble to the 500 handler.
            _logger.LogWarning(ex, "Atomic update of plan {PlanId} failed; transaction rolled back (nothing persisted)", id);
            return BadRequest(new
            {
                error = "invalid_plan_update",
                message = "Update rejected: one or more stops reference a place that does not exist. No changes were saved."
            });
        }

        _logger.LogInformation("Admin updated plan {PlanId}{StopInfo}",
            plan.Id, stopsReplaced ? $" (+{request.Stops!.Count} stops, atomic)" : "");

        if (stopsReplaced)
        {
            var detail = await _db.Plans.AsNoTracking()
                .Include(p => p.Stops)
                .ThenInclude(s => s.Place)
                .FirstAsync(p => p.Id == id, ct);
            return Ok(AdminPlanDetailDto.FromEntity(detail));
        }

        return Ok(AdminPlanDto.FromEntity(plan));
    }

    /// <summary>
    /// Resolves place names → ids, runs cheap validation (unresolved names, max-per-day)
    /// and builds the replacement PlanStop entities. PlaceId integrity is enforced by the
    /// FK at SaveChanges (inside the transaction), NOT pre-checked here — so a bad PlaceId
    /// rolls the whole atomic write back rather than persisting a partial update.
    /// </summary>
    private async Task<(List<PlanStop>? stops, IActionResult? error)> BuildStopsForReplaceAsync(
        Guid planId, List<CreatePlanStopRequest> requestStops, DateTimeOffset now, CancellationToken ct)
    {
        var placeNames = requestStops
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

        var unresolved = requestStops
            .Where(s => s.PlaceId == null && (string.IsNullOrEmpty(s.PlaceName) || !nameToId.ContainsKey(s.PlaceName!)))
            .ToList();
        if (unresolved.Count > 0)
        {
            var names = unresolved.Select(s => s.PlaceName ?? "(no name)");
            return (null, BadRequest(new { error = $"Could not resolve places: {string.Join(", ", names)}" }));
        }

        foreach (var day in requestStops.GroupBy(s => s.DayNumber))
        {
            if (day.Count() > PlanLimits.MaxStopsPerDay)
                return (null, BadRequest(new
                {
                    error = $"too_many_stops_day_{day.Key}",
                    message = $"Maximum {PlanLimits.MaxStopsPerDay} stops per day (day {day.Key} has {day.Count()})."
                }));
        }

        var stops = requestStops.Select(s => new PlanStop
        {
            Id = Guid.NewGuid(),
            PlanId = planId,
            PlaceId = s.PlaceId ?? nameToId[s.PlaceName!],
            DayNumber = s.DayNumber,
            OrderIndex = s.OrderIndex,
            TimeBlock = s.TimeBlock?.Trim(),
            SuggestedArrival = s.SuggestedArrival,
            SuggestedDurationMin = s.SuggestedDurationMin,
            CreatedAt = now
        }).ToList();

        return (stops, null);
    }

    [HttpPost("{id}/translate")]
    public async Task<IActionResult> TranslatePlan(Guid id, CancellationToken ct)
    {
        var plan = await _db.Plans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (plan == null) return NotFound(new { error = "Plan not found" });

        if (plan.Source != "curated")
            return BadRequest(new { error = "Translation is only supported for curated plans." });

        var draft = await _ai.TranslatePlanAsync(plan, "es", ct);
        if (draft == null)
            return StatusCode(503, new { error = "Translation service unavailable." });

        _logger.LogInformation("Translation: entity=Plan id={Id} lang=es action=draft", plan.Id);

        return Ok(new { nameEs = draft.Name, descriptionEs = draft.Description });
    }

    /// <summary>
    /// Backfill: translate all curated plans without ES translation.
    /// Idempotent — only processes plans missing name_i18n.es.
    /// </summary>
    [HttpPost("translate-batch")]
    public async Task<IActionResult> TranslateBatch([FromQuery] string lang = "es", [FromQuery] int limit = 10, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 50);

        var allCurated = await _db.Plans
            .Where(p => p.Source == "curated")
            .ToListAsync(ct);

        var toTranslate = allCurated
            .Where(p => p.NameI18n == null
                     || !p.NameI18n.RootElement.TryGetProperty(lang, out _))
            .ToList();

        if (toTranslate.Count == 0)
            return Ok(new { translated = 0, failed = 0, skipped = allCurated.Count,
                remaining = 0,
                message = $"All curated plans already have '{lang}' translation." });

        var totalPending = toTranslate.Count;
        var batch = toTranslate.Take(limit).ToList();
        var translated = 0;
        var failed = 0;

        foreach (var chunk in batch.Chunk(5))
        {
            foreach (var plan in chunk)
            {
                if (ct.IsCancellationRequested) break;

                var draft = await _ai.TranslatePlanAsync(plan, lang, ct);
                if (draft == null) { failed++; continue; }

                plan.NameI18n = LanguageAccessor.SetI18nString(plan.NameI18n, lang, draft.Name);
                plan.DescriptionI18n = LanguageAccessor.SetI18nString(plan.DescriptionI18n, lang, draft.Description);
                plan.TranslationStatus = LanguageAccessor.SetI18nString(plan.TranslationStatus, lang, "approved");
                plan.UpdatedAt = _clock.GetUtcNow();

                _logger.LogInformation("Translation: entity=Plan id={Id} lang={Lang} action=approved", plan.Id, lang);
                translated++;
            }

            // Save progress after each chunk — allows partial resumption on timeout
            if (!ct.IsCancellationRequested)
                await _db.SaveChangesAsync(ct);
        }

        _logger.LogInformation("translate-batch plans: translated={T} failed={F} skipped={S} remaining={R}",
            translated, failed, allCurated.Count - toTranslate.Count, totalPending - translated - failed);

        return Ok(new
        {
            translated,
            failed,
            skipped = allCurated.Count - toTranslate.Count,
            remaining = totalPending - translated - failed
        });
    }

    /// <summary>
    /// DEPRECATED: replace a plan's stops in isolation. Prefer the atomic
    /// <c>PATCH /admin/plans/{id}</c> with a <c>stops</c> field, which updates metadata
    /// and stops in one transaction. Kept until <c>locallist-admin</c> migrates off the
    /// two-call flow; do not build new callers against it.
    /// </summary>
    [Obsolete("Use PATCH /admin/plans/{id} with a `stops` field for an atomic metadata+stops update. Kept until admin migrates.")]
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

        return Ok(AdminPlanDetailDto.FromEntity(updatedPlan));
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
