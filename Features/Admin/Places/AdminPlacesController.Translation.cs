using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LocalList.API.NET.Shared.I18n;

namespace LocalList.API.NET.Features.Admin.Places;

// ES translation draft + batch translation + AI description suggestion. Endpoints here use
// _translator/_descGen directly (not shared with PlaceImportService). Logic is identical to
// the original single-file version; only its location changed.
public partial class AdminPlacesController
{
    [HttpPost("{id}/translate")]
    public async Task<IActionResult> TranslatePlace(Guid id, CancellationToken ct)
    {
        var place = await _db.Places.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (place == null) return NotFound(new { error = "Place not found" });

        if (place.Source != "curated")
            return BadRequest(new { error = "Translation is only supported for curated places." });

        var draft = await _translator.TranslatePlaceAsync(place, "es", ct);
        if (draft == null)
            return StatusCode(503, new { error = "Translation service unavailable." });

        _logger.LogInformation("Translation: entity=Place id={Id} lang=es action=draft", place.Id);

        return Ok(new
        {
            nameEs = draft.Name,
            whyThisPlaceEs = draft.WhyThisPlace,
            bestTimesEs = draft.BestTimes,
            neighborhoodEs = draft.Neighborhood,
            subcategoriesEs = draft.Subcategories,
            bestForEs = draft.BestFor,
            suitableForEs = draft.SuitableFor,
        });
    }

    [HttpPost("{id}/suggest-description")]
    public async Task<IActionResult> SuggestDescription(Guid id, CancellationToken ct)
    {
        var place = await _db.Places.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (place == null) return NotFound(new { error = "Place not found" });

        var description = await _descGen.GeneratePlaceDescriptionAsync(
            place.Name, place.City, place.Category, place.Subcategories?.FirstOrDefault(),
            null, place.GoogleRating, place.GoogleReviewCount, place.Neighborhood, ct);

        if (description == null)
            return StatusCode(503, new { error = "Description generation service unavailable." });

        _logger.LogInformation("SuggestDescription: place={PlaceId} chars={Len}", place.Id, description.Length);

        return Ok(new { whyThisPlace = description });
    }

    [HttpPost("translate-batch")]
    public async Task<IActionResult> TranslateBatch([FromQuery] string lang = "es", [FromQuery] int limit = 10, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 50);

        var allCurated = await _db.Places
            .Where(p => p.Source == "curated" && p.Status == "published")
            .ToListAsync(ct);

        var toTranslate = allCurated
            .Where(p => p.NameI18n == null
                     || !p.NameI18n.RootElement.TryGetProperty(lang, out _))
            .ToList();

        if (toTranslate.Count == 0)
            return Ok(new { translated = 0, failed = 0, skipped = allCurated.Count,
                remaining = 0,
                message = $"All published curated places already have '{lang}' translation." });

        var totalPending = toTranslate.Count;
        var batch = toTranslate.Take(limit).ToList();
        var translated = 0;
        var failed = 0;

        foreach (var chunk in batch.Chunk(5))
        {
            foreach (var place in chunk)
            {
                if (ct.IsCancellationRequested) break;

                var draft = await _translator.TranslatePlaceAsync(place, lang, ct);
                if (draft == null) { failed++; continue; }

                place.NameI18n = LanguageAccessor.SetI18nString(place.NameI18n, lang, draft.Name);
                place.WhyThisPlaceI18n = LanguageAccessor.SetI18nString(place.WhyThisPlaceI18n, lang, draft.WhyThisPlace);
                if (draft.BestTimes is { Count: > 0 })
                    place.BestTimesI18n = LanguageAccessor.SetI18nList(place.BestTimesI18n, lang, draft.BestTimes);
                place.NeighborhoodI18n = LanguageAccessor.SetI18nString(place.NeighborhoodI18n, lang, draft.Neighborhood);
                if (draft.Subcategories is { Count: > 0 })
                    place.SubcategoriesI18n = LanguageAccessor.SetI18nList(place.SubcategoriesI18n, lang, draft.Subcategories);
                place.BestForI18n = LanguageAccessor.SetI18nList(place.BestForI18n, lang, draft.BestFor);
                place.SuitableForI18n = LanguageAccessor.SetI18nList(place.SuitableForI18n, lang, draft.SuitableFor);
                place.TranslationStatus = LanguageAccessor.SetI18nString(place.TranslationStatus, lang, "approved");
                place.UpdatedAt = _clock.GetUtcNow();

                _logger.LogInformation("Translation: entity=Place id={Id} lang={Lang} action=approved", place.Id, lang);
                translated++;
            }

            // Save progress after each chunk — allows partial resumption on timeout
            if (!ct.IsCancellationRequested)
                await _db.SaveChangesAsync(ct);
        }

        _logger.LogInformation("translate-batch places: translated={T} failed={F} skipped={S} remaining={R}",
            translated, failed, allCurated.Count - toTranslate.Count, totalPending - translated - failed);

        return Ok(new
        {
            translated,
            failed,
            skipped = allCurated.Count - toTranslate.Count,
            remaining = totalPending - translated - failed
        });
    }
}
