using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using LocalList.API.NET.Shared.Auth;
using LocalList.API.NET.Shared.Data;
using LocalList.API.NET.Shared.Data.Entities;
using LocalList.API.NET.Features.Builder;
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
    private readonly AiProviderService _ai;

    public AdminPlansController(LocalListDbContext db, ILogger<AdminPlansController> logger, TimeProvider clock, AiProviderService ai)
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

        // i18n ES fields
        if (request.NameEs != null)
            plan.NameI18n = LanguageAccessor.SetI18nString(plan.NameI18n, "es", request.NameEs);
        if (request.DescriptionEs != null)
            plan.DescriptionI18n = LanguageAccessor.SetI18nString(plan.DescriptionI18n, "es", request.DescriptionEs);
        if (request.TranslationStatusEs != null)
            plan.TranslationStatus = LanguageAccessor.SetI18nString(plan.TranslationStatus, "es", request.TranslationStatusEs);

        plan.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Admin updated plan {PlanId}", plan.Id);

        return Ok(AdminPlanDto.FromEntity(plan));
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
    public async Task<IActionResult> TranslateBatch([FromQuery] string lang = "es", CancellationToken ct = default)
    {
        var allCurated = await _db.Plans
            .Where(p => p.Source == "curated")
            .ToListAsync(ct);

        var toTranslate = allCurated
            .Where(p => p.NameI18n == null
                     || !p.NameI18n.RootElement.TryGetProperty(lang, out _))
            .ToList();

        if (toTranslate.Count == 0)
            return Ok(new { translated = 0, failed = 0, skipped = allCurated.Count,
                message = $"All curated plans already have '{lang}' translation." });

        var translated = 0;
        var failed = 0;

        foreach (var plan in toTranslate)
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

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("translate-batch plans: translated={T} failed={F} skipped={S}",
            translated, failed, allCurated.Count - toTranslate.Count);

        return Ok(new
        {
            translated,
            failed,
            skipped = allCurated.Count - toTranslate.Count,
            remaining = toTranslate.Count - translated - failed
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
