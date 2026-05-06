using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using LocalList.API.NET.Shared.Auth;
using LocalList.API.NET.Shared.Data;
using LocalList.API.NET.Shared.Data.Entities;
using LocalList.API.NET.Features.Builder;
using LocalList.API.NET.Features.Builder.Services;
using LocalList.API.NET.Shared.I18n;
using LocalList.API.NET.Shared.Taxonomy;

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
    private readonly AiProviderService _ai;

    private static readonly HashSet<string> ValidStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "draft", "in_review", "published", "rejected"
    };

    public AdminPlacesController(
        LocalListDbContext db,
        ILogger<AdminPlacesController> logger,
        TimeProvider clock,
        EmbeddingService embeddings,
        AiProviderService ai)
    {
        _db = db;
        _logger = logger;
        _clock = clock;
        _embeddings = embeddings;
        _ai = ai;
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

        if (!string.IsNullOrEmpty(search))
            query = query.Where(p => p.Name.Contains(search));

        var total = await query.CountAsync(ct);

        var places = await query
            .OrderByDescending(p => p.CreatedAt)
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

        if (!PlaceTaxonomy.IsValidSubcategory(request.Category, request.Subcategory))
            return BadRequest(new { error = $"Invalid subcategory '{request.Subcategory}' for category '{request.Category}'.", code = "subcategory_not_in_taxonomy" });

        var userId = await GetUserIdAsync(ct);
        var now = _clock.GetUtcNow();

        var place = new Place
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            Category = request.Category,
            WhyThisPlace = request.WhyThisPlace.Trim(),
            Subcategory = request.Subcategory?.Trim(),
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

        var userId = await GetUserIdAsync(ct);
        var now = _clock.GetUtcNow();
        var results = new List<BulkImportItemResult>();
        int created = 0, skipped = 0, errors = 0;

        // Load existing GooglePlaceIds for dedup
        var incomingGoogleIds = requests
            .Where(r => !string.IsNullOrEmpty(r.GooglePlaceId))
            .Select(r => r.GooglePlaceId!)
            .ToHashSet();

        var existingGoogleIds = incomingGoogleIds.Count > 0
            ? (await _db.Places.AsNoTracking()
                .Where(p => p.GooglePlaceId != null && incomingGoogleIds.Contains(p.GooglePlaceId))
                .Select(p => p.GooglePlaceId!)
                .ToListAsync(ct))
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Load existing Name+City combos for dedup (places without GooglePlaceId)
        var incomingNameCities = requests
            .Where(r => string.IsNullOrEmpty(r.GooglePlaceId))
            .Select(r => (r.Name.Trim().ToLowerInvariant(), (r.City?.Trim() ?? "Miami").ToLowerInvariant()))
            .ToHashSet();

        var existingNameCities = incomingNameCities.Count > 0
            ? (await _db.Places.AsNoTracking()
                .Where(p => p.GooglePlaceId == null)
                .Select(p => new { p.Name, p.City })
                .ToListAsync(ct))
                .Select(p => (p.Name.ToLowerInvariant(), p.City.ToLowerInvariant()))
                .ToHashSet()
            : new HashSet<(string, string)>();

        var placesToAdd = new List<Place>();

        foreach (var request in requests)
        {
            // Validate category
            if (!PlaceTaxonomy.IsValidCategory(request.Category))
            {
                results.Add(new BulkImportItemResult(request.Name, "error", $"Invalid category: {request.Category}", null));
                errors++;
                continue;
            }

            // Validate subcategory against whitelist for this category
            if (!PlaceTaxonomy.IsValidSubcategory(request.Category, request.Subcategory))
            {
                results.Add(new BulkImportItemResult(request.Name, "error", $"Invalid subcategory '{request.Subcategory}' for category '{request.Category}'", null));
                errors++;
                continue;
            }

            // Dedup by GooglePlaceId
            if (!string.IsNullOrEmpty(request.GooglePlaceId) && existingGoogleIds.Contains(request.GooglePlaceId))
            {
                results.Add(new BulkImportItemResult(request.Name, "skipped_duplicate", "GooglePlaceId already exists", null));
                skipped++;
                continue;
            }

            // Dedup by Name+City
            var nameKey = request.Name.Trim().ToLowerInvariant();
            var cityKey = (request.City?.Trim() ?? "Miami").ToLowerInvariant();
            if (string.IsNullOrEmpty(request.GooglePlaceId) && existingNameCities.Contains((nameKey, cityKey)))
            {
                results.Add(new BulkImportItemResult(request.Name, "skipped_duplicate", "Name+City already exists", null));
                skipped++;
                continue;
            }

            var place = new Place
            {
                Id = Guid.NewGuid(),
                Name = request.Name.Trim(),
                Category = request.Category,
                WhyThisPlace = request.WhyThisPlace.Trim(),
                Subcategory = request.Subcategory?.Trim(),
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
                Flags = request.Flags,
                SubmittedById = userId,
                CreatedAt = now,
                UpdatedAt = now
            };

            placesToAdd.Add(place);
            existingGoogleIds.Add(request.GooglePlaceId ?? "");
            existingNameCities.Add((nameKey, cityKey));

            results.Add(new BulkImportItemResult(request.Name, "created", null, place.Id));
            created++;
        }

        if (placesToAdd.Count > 0)
        {
            _db.Places.AddRange(placesToAdd);
            await _db.SaveChangesAsync(ct);
        }

        _logger.LogInformation("Bulk import: {Created} created, {Skipped} skipped, {Errors} errors", created, skipped, errors);

        return Ok(new BulkImportResult(created, skipped, errors, results));
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
        if (request.Subcategory != null && !PlaceTaxonomy.IsValidSubcategory(effectiveCategory, request.Subcategory))
            return BadRequest(new { error = $"Invalid subcategory '{request.Subcategory}' for category '{effectiveCategory}'.", code = "subcategory_not_in_taxonomy" });

        // Apply non-null fields only (partial update)
        if (request.Name != null) place.Name = request.Name.Trim();
        if (request.Category != null) place.Category = request.Category;
        if (request.WhyThisPlace != null) place.WhyThisPlace = request.WhyThisPlace.Trim();
        if (request.Subcategory != null) place.Subcategory = request.Subcategory.Trim();
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
        if (request.SubcategoryEs != null)
            place.SubcategoryI18n = LanguageAccessor.SetI18nString(place.SubcategoryI18n, "es", request.SubcategoryEs);
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
        place.ReviewedById = await GetUserIdAsync(ct);
        place.UpdatedAt = _clock.GetUtcNow();

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Admin {Action} place {PlaceId}", request.Status, place.Id);

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

    private async Task<Guid?> GetUserIdAsync(CancellationToken ct = default)
    {
        return await User.GetUserIdAsync(_db, ct);
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
                p.Name, p.Category, p.Subcategory, p.Neighborhood, p.City, p.WhyThisPlace, p.BestFor, p.SuitableFor))
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

    [HttpPost("{id}/translate")]
    public async Task<IActionResult> TranslatePlace(Guid id, CancellationToken ct)
    {
        var place = await _db.Places.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (place == null) return NotFound(new { error = "Place not found" });

        if (place.Source != "curated")
            return BadRequest(new { error = "Translation is only supported for curated places." });

        var draft = await _ai.TranslatePlaceAsync(place, "es", ct);
        if (draft == null)
            return StatusCode(503, new { error = "Translation service unavailable." });

        _logger.LogInformation("Translation: entity=Place id={Id} lang=es action=draft", place.Id);

        return Ok(new
        {
            nameEs = draft.Name,
            whyThisPlaceEs = draft.WhyThisPlace,
            bestTimeEs = draft.BestTime,
            neighborhoodEs = draft.Neighborhood,
            subcategoryEs = draft.Subcategory,
            bestForEs = draft.BestFor,
            suitableForEs = draft.SuitableFor,
        });
    }

    /// <summary>
    /// Backfill: translate all curated places without ES translation.
    /// Idempotent — only processes places missing name_i18n.es.
    /// Saves progress every 5 places so partial runs are resumable.
    /// </summary>
    [HttpPost("translate-batch")]
    public async Task<IActionResult> TranslateBatch([FromQuery] string lang = "es", CancellationToken ct = default)
    {
        var allCurated = await _db.Places
            .Where(p => p.Source == "curated" && p.Status == "published")
            .ToListAsync(ct);

        var toTranslate = allCurated
            .Where(p => p.NameI18n == null
                     || !p.NameI18n.RootElement.TryGetProperty(lang, out _))
            .ToList();

        if (toTranslate.Count == 0)
            return Ok(new { translated = 0, failed = 0, skipped = allCurated.Count,
                message = $"All published curated places already have '{lang}' translation." });

        var translated = 0;
        var failed = 0;

        foreach (var chunk in toTranslate.Chunk(5))
        {
            foreach (var place in chunk)
            {
                if (ct.IsCancellationRequested) break;

                var draft = await _ai.TranslatePlaceAsync(place, lang, ct);
                if (draft == null) { failed++; continue; }

                place.NameI18n = LanguageAccessor.SetI18nString(place.NameI18n, lang, draft.Name);
                place.WhyThisPlaceI18n = LanguageAccessor.SetI18nString(place.WhyThisPlaceI18n, lang, draft.WhyThisPlace);
                place.BestTimeI18n = LanguageAccessor.SetI18nString(place.BestTimeI18n, lang, draft.BestTime);
                place.NeighborhoodI18n = LanguageAccessor.SetI18nString(place.NeighborhoodI18n, lang, draft.Neighborhood);
                place.SubcategoryI18n = LanguageAccessor.SetI18nString(place.SubcategoryI18n, lang, draft.Subcategory);
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

        _logger.LogInformation("translate-batch places: translated={T} failed={F} skipped={S}",
            translated, failed, allCurated.Count - toTranslate.Count);

        return Ok(new
        {
            translated,
            failed,
            skipped = allCurated.Count - toTranslate.Count,
            remaining = toTranslate.Count - translated - failed
        });
    }
}
