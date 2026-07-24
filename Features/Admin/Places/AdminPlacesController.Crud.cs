using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LocalList.API.NET.Shared.Auth;
using LocalList.API.NET.Shared.Data.Entities;
using LocalList.API.NET.Shared.Dtos;
using LocalList.API.NET.Shared.I18n;
using LocalList.API.NET.Shared.Taxonomy;

namespace LocalList.API.NET.Features.Admin.Places;

// Create/bulk-import/update/review/postpone/delete for places. Logic is identical to the
// original single-file version; only its location changed.
public partial class AdminPlacesController
{
    [HttpPost]
    public async Task<IActionResult> CreatePlace([FromBody] CreatePlaceRequest request, CancellationToken ct)
    {
        if (!PlaceTaxonomy.IsValidCategory(request.Category))
            return BadRequest(new { error = $"Invalid category. Valid: {string.Join(", ", PlaceTaxonomy.Categories)}" });

        var resolvedSubs = request.Subcategories?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
        if (!await _taxonomy.AreValidSubcategoriesAsync(request.Category, resolvedSubs, ct))
            return BadRequest(new { error = $"Invalid subcategory for category '{request.Category}'.", code = "subcategory_not_in_taxonomy" });

        var userId = await User.GetUserIdAsync(_db, ct);
        var now = _clock.GetUtcNow();

        var place = new Place
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            Category = request.Category,
            WhyThisPlace = request.WhyThisPlace?.Trim() ?? string.Empty,
            Subcategories = resolvedSubs,
            Neighborhood = request.Neighborhood?.Trim(),
            City = request.City?.Trim() ?? "Miami",
            Latitude = request.Latitude,
            Longitude = request.Longitude,
            BestFor = request.BestFor,
            SuitableFor = request.SuitableFor,
            BestTimes = request.BestTimes,
            PriceRange = request.PriceRange?.Trim(),
            // T3: barrido, nunca persistir una URL de Google (key) ni el preview admin-authed.
            Photos = PlacePhotoUrls.SanitizeForStorage(request.Photos),
            GooglePlaceId = request.GooglePlaceId?.Trim(),
            GoogleRating = request.GoogleRating,
            GoogleReviewCount = request.GoogleReviewCount,
            Source = request.Source?.Trim() ?? "curated",
            SourceUrl = request.SourceUrl?.Trim(),
            Status = request.Status?.Trim() ?? "in_review",
            AiVibeScore = request.AiVibeScore,
            VisitDurationMin = request.VisitDurationMin,
            Flags = request.Flags,
            SubmittedById = userId,
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.Places.Add(place);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Admin created place {PlaceId} ({Name})", place.Id, place.Name);

        return CreatedAtAction(nameof(GetPlace), new { id = place.Id }, AdminPlaceDto.FromEntity(place));
    }

    [HttpPost("bulk")]
    public async Task<IActionResult> BulkImport([FromBody] List<CreatePlaceRequest> requests, CancellationToken ct)
    {
        if (requests.Count == 0)
            return BadRequest(new { error = "Empty request list." });
        if (requests.Count > 500)
            return BadRequest(new { error = "Maximum 500 places per bulk import." });

        var userId = await User.GetUserIdAsync(_db, ct);
        var result = await _importSvc.BulkImportAsync(requests, userId, _clock.GetUtcNow(), ct);
        return Ok(result);
    }

    /// <summary>
    /// Resolves Google Maps URLs / Place IDs to full place details and bulk-imports them.
    /// Accepts up to 500 URLs per request. Deduplicates by GooglePlaceId.
    /// </summary>
    [HttpPost("import-from-urls")]
    public async Task<IActionResult> ImportFromUrls([FromBody] ImportFromUrlsRequest request, CancellationToken ct)
    {
        if (request.Urls.Count == 0)
            return BadRequest(new { error = "Empty URL list." });
        if (request.Urls.Count > 500)
            return BadRequest(new { error = "Maximum 500 URLs per request." });

        var userId = await User.GetUserIdAsync(_db, ct);
        var result = await _importSvc.ImportFromUrlsAsync(request, userId, _clock.GetUtcNow(), ct);
        return Ok(result);
    }

    [HttpPatch("{id}")]
    public async Task<IActionResult> UpdatePlace(Guid id, [FromBody] UpdatePlaceRequest request, CancellationToken ct)
    {
        var place = await _db.Places.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (place == null)
            return NotFound(new { error = "Place not found" });

        if (request.Category != null && !PlaceTaxonomy.IsValidCategory(request.Category))
            return BadRequest(new { error = $"Invalid category. Valid: {string.Join(", ", PlaceTaxonomy.Categories)}" });

        var effectiveCategory = request.Category ?? place.Category;
        var resolvedUpdateSubs = request.Subcategories?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
        if (resolvedUpdateSubs != null && !await _taxonomy.AreValidSubcategoriesAsync(effectiveCategory, resolvedUpdateSubs, ct))
            return BadRequest(new { error = $"Invalid subcategory for category '{effectiveCategory}'.", code = "subcategory_not_in_taxonomy" });

        // Apply non-null fields only (partial update)
        if (request.Name != null) place.Name = request.Name.Trim();
        if (request.Category != null) place.Category = request.Category;
        if (request.WhyThisPlace != null) place.WhyThisPlace = request.WhyThisPlace.Trim();
        if (resolvedUpdateSubs != null) place.Subcategories = resolvedUpdateSubs;
        if (request.Neighborhood != null) place.Neighborhood = request.Neighborhood.Trim();
        if (request.City != null) place.City = request.City.Trim();
        if (request.Latitude.HasValue) place.Latitude = request.Latitude;
        if (request.Longitude.HasValue) place.Longitude = request.Longitude;
        if (request.BestFor != null) place.BestFor = request.BestFor;
        if (request.SuitableFor != null) place.SuitableFor = request.SuitableFor;
        if (request.BestTimes != null) place.BestTimes = request.BestTimes;
        if (request.PriceRange != null) place.PriceRange = request.PriceRange.Trim();
        // T3: barrido, nunca persistir una URL de Google (key) ni el preview admin-authed.
        if (request.Photos != null) place.Photos = PlacePhotoUrls.SanitizeForStorage(request.Photos);
        if (request.GooglePlaceId != null) place.GooglePlaceId = request.GooglePlaceId.Trim();
        if (request.GoogleRating.HasValue) place.GoogleRating = request.GoogleRating;
        if (request.GoogleReviewCount.HasValue) place.GoogleReviewCount = request.GoogleReviewCount;
        if (request.Source != null) place.Source = request.Source.Trim();
        if (request.SourceUrl != null) place.SourceUrl = request.SourceUrl.Trim();
        if (request.AiVibeScore.HasValue) place.AiVibeScore = request.AiVibeScore;
        if (request.VisitDurationMin.HasValue) place.VisitDurationMin = request.VisitDurationMin;
        if (request.Flags != null) place.Flags = request.Flags;

        // i18n ES fields
        if (request.NameEs != null)
            place.NameI18n = LanguageAccessor.SetI18nString(place.NameI18n, "es", request.NameEs);
        if (request.WhyThisPlaceEs != null)
            place.WhyThisPlaceI18n = LanguageAccessor.SetI18nString(place.WhyThisPlaceI18n, "es", request.WhyThisPlaceEs);
        if (request.BestTimesEs != null)
            place.BestTimesI18n = LanguageAccessor.SetI18nList(place.BestTimesI18n, "es", request.BestTimesEs);
        if (request.NeighborhoodEs != null)
            place.NeighborhoodI18n = LanguageAccessor.SetI18nString(place.NeighborhoodI18n, "es", request.NeighborhoodEs);
        if (request.SubcategoriesEs != null)
            place.SubcategoriesI18n = LanguageAccessor.SetI18nList(place.SubcategoriesI18n, "es", request.SubcategoriesEs);
        if (request.BestForEs != null)
            place.BestForI18n = LanguageAccessor.SetI18nList(place.BestForI18n, "es", request.BestForEs);
        if (request.SuitableForEs != null)
            place.SuitableForI18n = LanguageAccessor.SetI18nList(place.SuitableForI18n, "es", request.SuitableForEs);
        if (request.TranslationStatusEs != null)
            place.TranslationStatus = LanguageAccessor.SetI18nString(place.TranslationStatus, "es", request.TranslationStatusEs);

        place.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Admin updated place {PlaceId}", place.Id);

        return Ok(AdminPlaceDto.FromEntity(place));
    }

    [HttpPatch("{id}/review")]
    public async Task<IActionResult> ReviewPlace(Guid id, [FromBody] ReviewPlaceRequest request, CancellationToken ct)
    {
        var place = await _db.Places.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (place == null)
            return NotFound(new { error = "Place not found" });

        if (request.Status == "rejected" && string.IsNullOrWhiteSpace(request.RejectionReason))
            return BadRequest(new { error = "RejectionReason is required when rejecting a place." });

        place.Status = request.Status;
        place.RejectionReason = request.Status == "rejected" ? request.RejectionReason?.Trim() : null;
        place.ReviewedById = await User.GetUserIdAsync(_db, ct);
        place.UpdatedAt = _clock.GetUtcNow();

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Admin {Action} place {PlaceId}", request.Status, place.Id);

        return Ok(AdminPlaceDto.FromEntity(place));
    }

    [HttpPatch("{id}/postpone")]
    public async Task<IActionResult> PostponePlace(Guid id, CancellationToken ct)
    {
        var place = await _db.Places.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (place == null)
            return NotFound(new { error = "Place not found" });

        place.ReviewDeferredAt = _clock.GetUtcNow();
        place.UpdatedAt = _clock.GetUtcNow();
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Admin postponed place {PlaceId}", place.Id);
        return Ok(AdminPlaceDto.FromEntity(place));
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeletePlace(Guid id, [FromQuery] bool hard = false, CancellationToken ct = default)
    {
        var place = await _db.Places
            .Include(p => p.PlanStops)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

        if (place == null)
            return NotFound(new { error = "Place not found" });

        if (hard)
        {
            if (place.PlanStops.Count > 0)
                return Conflict(new { error = "Cannot hard-delete a place that is referenced by plan stops. Remove the plan stops first or use soft delete." });

            _db.Places.Remove(place);
        }
        else
        {
            place.Status = "deleted";
            place.UpdatedAt = _clock.GetUtcNow();
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Admin {Action} place {PlaceId}", hard ? "hard-deleted" : "soft-deleted", place.Id);

        return NoContent();
    }
}
