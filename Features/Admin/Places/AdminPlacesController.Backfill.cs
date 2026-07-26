using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using LocalList.API.NET.Shared.AI.Services;
using LocalList.API.NET.Shared.Taxonomy;

namespace LocalList.API.NET.Features.Admin.Places;

// Embeddings reindex + opening-hours/description backfills. Logic is identical to the
// original single-file version; only its location changed.
public partial class AdminPlacesController
{
    [HttpPost("reindex-embeddings")]
    public async Task<IActionResult> ReindexEmbeddings(
        [FromQuery] bool onlyMissing = false,
        [FromQuery] int limit = 1000,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 5000);

        var query = _db.Places.AsQueryable();
        if (onlyMissing) query = query.Where(p => p.Embedding == null);

        var ids = await query
            .OrderBy(p => p.CreatedAt)
            .Take(limit)
            .Select(p => p.Id)
            .ToListAsync(ct);

        if (ids.Count == 0)
        {
            return Ok(new { reindexed = 0, failed = 0, total = 0 });
        }

        // Single tracked query — avoids N+1 y race conditions entre proyección y reload.
        var tracked = await _db.Places
            .Where(p => ids.Contains(p.Id))
            .ToListAsync(ct);

        var texts = tracked
            .Select(p => EmbeddingService.BuildPlaceIndexText(
                p.Name, p.Category, p.Subcategories,
                p.Neighborhood, p.City, p.WhyThisPlace, p.BestFor, p.SuitableFor))
            .ToList();

        var vectors = await _embeddings.EmbedBatchAsync(texts, ct);

        if (vectors.Count != tracked.Count)
        {
            _logger.LogError(
                "Embedding batch size mismatch — expected {Expected} got {Got}",
                tracked.Count, vectors.Count);
            return StatusCode(502, new { error = "embedding_provider_failure" });
        }

        var now = _clock.GetUtcNow();
        for (var i = 0; i < tracked.Count; i++)
        {
            tracked[i].Embedding = vectors[i];
            tracked[i].UpdatedAt = now;
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Reindexed embeddings: {Reindexed}/{Total} (onlyMissing={OnlyMissing}, limit={Limit})",
            tracked.Count, tracked.Count, onlyMissing, limit);

        return Ok(new { reindexed = tracked.Count, failed = 0, total = tracked.Count });
    }

    [HttpPost("backfill-opening-hours")]
    public async Task<IActionResult> BackfillOpeningHours(
        [FromQuery] bool onlyMissing = true,
        [FromQuery] int limit = 100,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 500);

        var query = _db.Places.Where(p => p.GooglePlaceId != null);
        if (onlyMissing) query = query.Where(p => p.OpeningHours == null);

        var places = await query
            .OrderBy(p => p.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);

        if (places.Count == 0)
            return Ok(new { backfilled = 0, failed = 0, skipped = 0, total = 0 });

        int backfilled = 0, failed = 0;
        var now = _clock.GetUtcNow();

        foreach (var chunk in places.Chunk(10))
        {
            foreach (var place in chunk)
            {
                if (ct.IsCancellationRequested) break;

                var details = await _googlePlaces.GetDetailsAsync(place.GooglePlaceId!, ct);
                if (details?.OpeningHours is null) { failed++; continue; }

                place.OpeningHours = details.OpeningHours.ToJsonDocument();
                place.UpdatedAt = now;
                backfilled++;
            }

            if (!ct.IsCancellationRequested)
                await _db.SaveChangesAsync(ct);
        }

        _logger.LogInformation(
            "backfill-opening-hours: backfilled={B} failed={F} total={T} onlyMissing={M}",
            backfilled, failed, places.Count, onlyMissing);

        return Ok(new
        {
            backfilled,
            failed,
            skipped = places.Count - backfilled - failed,
            total = places.Count
        });
    }

    [HttpPost("backfill-descriptions")]
    public async Task<IActionResult> BackfillDescriptions(
        [FromQuery] bool dryRun = true,
        [FromQuery] int limit = 50,
        [FromQuery] int geminiLimit = 6,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 200);
        // geminiLimit caps the Gemini-fallback bucket per call. Each Gemini request has a 4s
        // inter-request delay (15 RPM free-tier guard), so 6 places ≈ 24s — safely under the
        // 40s Railway proxy timeout. Increase only on a paid API key with higher RPM.
        geminiLimit = Math.Clamp(geminiLimit, 1, 20);

        var candidates = await _db.Places
            .Where(p => p.Status != "rejected" &&
                        (p.WhyThisPlace == "" || p.WhyThisPlace == PlaceTaxonomy.GooglePlaceholderWhyThisPlace))
            .OrderBy(p => p.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);

        var bucketGoogle = candidates.Where(p => p.GooglePlaceId != null).ToList();
        // Cap Gemini bucket to avoid proxy timeouts (4s/request × geminiLimit ≤ ~40s).
        var bucketGemini = candidates.Where(p => p.GooglePlaceId == null).Take(geminiLimit).ToList();

        var geminiSkipped = candidates.Count(p => p.GooglePlaceId == null) - bucketGemini.Count;
        if (dryRun)
            return Ok(new
            {
                candidates = candidates.Count,
                wouldFetchGoogle = bucketGoogle.Count,
                wouldFallbackGemini = bucketGemini.Count,
                geminiSkippedByLimit = geminiSkipped,
                dryRun = true
            });

        // Clear legacy placeholder rows before processing so they never surface in the app
        var clearedLegacy = await _db.Places
            .Where(p => p.WhyThisPlace == PlaceTaxonomy.GooglePlaceholderWhyThisPlace)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.WhyThisPlace, _ => ""), ct);
        _logger.LogInformation("backfill-descriptions: clearedLegacyPlaceholder={N}", clearedLegacy);

        int googleFilled = 0, geminiFilled = 0, failed = 0;
        var errors = new List<object>();
        var now = _clock.GetUtcNow();

        // Bucket A: try Google editorial first (chunks of 10)
        foreach (var chunk in bucketGoogle.Chunk(10))
        {
            foreach (var place in chunk)
            {
                if (ct.IsCancellationRequested) break;
                var details = await _googlePlaces.GetDetailsAsync(place.GooglePlaceId!, ct);
                if (!string.IsNullOrWhiteSpace(details?.EditorialSummary))
                {
                    place.WhyThisPlace = details.EditorialSummary;
                    place.UpdatedAt = now;
                    googleFilled++;
                }
                else
                {
                    bucketGemini.Add(place);
                }
            }
            if (!ct.IsCancellationRequested)
                await _db.SaveChangesAsync(ct);
        }

        // Bucket B: Gemini fallback — sequential with delay to stay under free-tier limit (~15 RPM)
        foreach (var place in bucketGemini)
        {
            if (ct.IsCancellationRequested) break;
            var result = await _descGen.GeneratePlaceDescriptionWithDiagnosticsAsync(
                place.Name, place.City, place.Category, place.Subcategories?.FirstOrDefault(),
                null, place.GoogleRating, place.GoogleReviewCount, place.Neighborhood, ct);
            if (result.Description != null)
            {
                place.WhyThisPlace = result.Description;
                place.UpdatedAt = now;
                geminiFilled++;
            }
            else
            {
                failed++;
                if (errors.Count < 20)
                    errors.Add(new { placeId = place.Id, name = place.Name, kind = result.ErrorKind, message = result.ErrorMessage });
            }
            await Task.Delay(4000, ct);
        }
        if (!ct.IsCancellationRequested)
            await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "backfill-descriptions: googleFilled={G} geminiFilled={Gem} failed={F} total={T}",
            googleFilled, geminiFilled, failed, candidates.Count);

        return Ok(new
        {
            candidates = candidates.Count,
            clearedLegacyPlaceholder = clearedLegacy,
            googleFilled,
            geminiFilled,
            failed,
            errors,
            errorsTruncated = failed > errors.Count,
            geminiSkippedByLimit = geminiSkipped,
            dryRun = false
        });
    }
}
