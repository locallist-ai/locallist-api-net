using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using LocalList.API.NET.Shared.Auth;
using LocalList.API.NET.Shared.Constants;
using LocalList.API.NET.Shared.Data;
using LocalList.API.NET.Shared.Data.Entities;
using LocalList.API.NET.Features.Builder.Services;
using LocalList.API.NET.Shared.I18n;
using LocalList.API.NET.Shared.Search;
using LocalList.API.NET.Shared.Taxonomy;
using ITaxonomySvc = LocalList.API.NET.Shared.Taxonomy.ITaxonomyService;

namespace LocalList.API.NET.Features.Admin.Places;

[ApiController]
[Route("admin/places")]
[AdminAuthorize]
[EnableRateLimiting("AdminLimit")]
public class AdminPlacesController : ControllerBase
{
    private readonly LocalListDbContext _db;
    private readonly ILogger<AdminPlacesController> _logger;
    private readonly TimeProvider _clock;
    private readonly EmbeddingService _embeddings;
    private readonly PlaceTranslatorService _translator;
    private readonly DescriptionGeneratorService _descGen;
    private readonly IGooglePlacesService _googlePlaces;
    private readonly ITaxonomySvc _taxonomy;
    private readonly PlaceImportService _importSvc;

    private static readonly HashSet<string> ValidStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "draft", "in_review", "published", "rejected"
    };

    public AdminPlacesController(
        LocalListDbContext db,
        ILogger<AdminPlacesController> logger,
        TimeProvider clock,
        EmbeddingService embeddings,
        PlaceTranslatorService translator,
        DescriptionGeneratorService descGen,
        IGooglePlacesService googlePlaces,
        ITaxonomySvc taxonomy,
        PlaceImportService importSvc)
    {
        _db = db;
        _logger = logger;
        _clock = clock;
        _embeddings = embeddings;
        _translator = translator;
        _descGen = descGen;
        _googlePlaces = googlePlaces;
        _taxonomy = taxonomy;
        _importSvc = importSvc;
    }

    [HttpGet("cities")]
    public async Task<IActionResult> GetCities(CancellationToken ct)
    {
        var cities = await _db.Places
            .Where(p => p.City != null)
            .Select(p => p.City)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync(ct);

        return Ok(new { cities });
    }

    [HttpPost("google-search")]
    public async Task<IActionResult> GoogleSearch([FromBody] GoogleSearchRequest request, CancellationToken ct)
    {
        var textQuery = $"{request.Query.Trim()} in {request.City.Trim()}";
        var previews = await _googlePlaces.SearchAsync(textQuery, ct);

        if (previews is null)
            return NotFound(new { error = "google_places_unavailable", message = "Google Places API key not configured or service unavailable." });

        if (previews.Count > 0)
        {
            var incomingIds = previews.Select(p => p.GooglePlaceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var existing = (await _db.Places.AsNoTracking()
                .Where(p => p.GooglePlaceId != null && incomingIds.Contains(p.GooglePlaceId))
                .Select(p => p.GooglePlaceId!)
                .ToListAsync(ct))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            previews = previews
                .Select(p => p with { ExistsInLib = existing.Contains(p.GooglePlaceId) })
                .ToList();
        }

        _logger.LogInformation("GoogleSearch: query='{Query}' returned {Count} results", textQuery, previews.Count);
        return Ok(new { results = previews });
    }

    [HttpGet]
    public async Task<IActionResult> GetPlaces(
        [FromQuery] string? status,
        [FromQuery] string? city,
        [FromQuery] string? category,
        [FromQuery] string? search,
        [FromQuery] int limit = 50,
        [FromQuery] int offset = 0,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 200);

        var query = _db.Places.AsNoTracking().AsQueryable();

        if (!string.IsNullOrEmpty(status))
            query = query.Where(p => p.Status == status);

        if (!string.IsNullOrEmpty(city))
            query = query.Where(p => p.City == city);

        if (!string.IsNullOrEmpty(category))
            query = query.Where(p => p.Category == category);

        var escapedSearch = LikePatterns.Normalize(search);
        if (!string.IsNullOrEmpty(escapedSearch))
            query = query.Where(p => EF.Functions.ILike(p.Name, $"%{escapedSearch}%", @"\"));

        var total = await query.CountAsync(ct);

        var ordered = status == "in_review"
            ? query.OrderBy(p => p.ReviewDeferredAt == null)
                   .ThenByDescending(p => p.ReviewDeferredAt)
                   .ThenByDescending(p => p.CreatedAt)
            : query.OrderByDescending(p => p.CreatedAt);

        var places = await ordered
            .Skip(offset)
            .Take(limit)
            .Select(p => AdminPlaceDto.FromEntity(p))
            .ToListAsync(ct);

        return Ok(new { places, total });
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetPlace(Guid id, CancellationToken ct)
    {
        var place = await _db.Places.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id, ct);

        if (place == null)
            return NotFound(new { error = "Place not found" });

        return Ok(AdminPlaceDto.FromEntity(place));
    }

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
            BestTime = request.BestTime?.Trim(),
            PriceRange = request.PriceRange?.Trim(),
            Photos = request.Photos,
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
        if (request.BestTime != null) place.BestTime = request.BestTime.Trim();
        if (request.PriceRange != null) place.PriceRange = request.PriceRange.Trim();
        if (request.Photos != null) place.Photos = request.Photos;
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
        if (request.BestTimeEs != null)
            place.BestTimeI18n = LanguageAccessor.SetI18nString(place.BestTimeI18n, "es", request.BestTimeEs);
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
            bestTimeEs = draft.BestTime,
            neighborhoodEs = draft.Neighborhood,
            subcategoriesEs = draft.Subcategory != null ? new List<string> { draft.Subcategory } : null,
            bestForEs = draft.BestFor,
            suitableForEs = draft.SuitableFor,
        });
    }

    // ── Endpoints below this line use _ai/_embeddings/_googlePlaces directly
    // ── (not shared with PlaceImportService) ──────────────────────────────

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


    [HttpPost("backfill-descriptions")]
    public async Task<IActionResult> BackfillDescriptions(
        [FromQuery] bool dryRun = true,
        [FromQuery] int limit = 50,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 200);

        var candidates = await _db.Places
            .Where(p => p.Status != "rejected" &&
                        (p.WhyThisPlace == "" || p.WhyThisPlace == PlaceTaxonomy.GooglePlaceholderWhyThisPlace))
            .OrderBy(p => p.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);

        var bucketGoogle = candidates.Where(p => p.GooglePlaceId != null).ToList();
        var bucketGemini = candidates.Where(p => p.GooglePlaceId == null).ToList();

        if (dryRun)
            return Ok(new
            {
                candidates = candidates.Count,
                wouldFetchGoogle = bucketGoogle.Count,
                wouldFallbackGemini = bucketGemini.Count,
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

        return Ok(new { candidates = candidates.Count, clearedLegacyPlaceholder = clearedLegacy, googleFilled, geminiFilled, failed, errors, dryRun = false });
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
                place.BestTimeI18n = LanguageAccessor.SetI18nString(place.BestTimeI18n, lang, draft.BestTime);
                place.NeighborhoodI18n = LanguageAccessor.SetI18nString(place.NeighborhoodI18n, lang, draft.Neighborhood);
                if (draft.Subcategory != null)
                    place.SubcategoriesI18n = LanguageAccessor.SetI18nList(place.SubcategoriesI18n, lang, new List<string> { draft.Subcategory });
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
