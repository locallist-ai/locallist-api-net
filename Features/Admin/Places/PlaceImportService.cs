using Microsoft.EntityFrameworkCore;
using LocalList.API.NET.Shared.AI.Services;
using LocalList.API.NET.Shared.Data;
using LocalList.API.NET.Shared.Data.Entities;
using LocalList.API.NET.Shared.Photos;
using LocalList.API.NET.Shared.Taxonomy;
using ITaxonomySvc = LocalList.API.NET.Shared.Taxonomy.ITaxonomyService;

namespace LocalList.API.NET.Features.Admin.Places;

/// <summary>
/// Encapsulates bulk-import business logic shared by POST /admin/places/bulk
/// and POST /admin/places/import-from-urls: validation, dedup, description filling,
/// DB insert, and inline embedding generation.
///
/// HTTP concerns (input size limits, auth, response mapping) stay in the controller.
/// </summary>
public class PlaceImportService
{
    private readonly LocalListDbContext _db;
    private readonly IDescriptionGeneratorService _ai;
    private readonly EmbeddingService _embeddings;
    private readonly IGooglePlacesService _googlePlaces;
    private readonly ITaxonomySvc _taxonomy;
    private readonly IPhotoRehostService _photoRehost;
    private readonly ILogger<PlaceImportService> _logger;
    private readonly TimeProvider _clock;

    public PlaceImportService(
        LocalListDbContext db,
        IDescriptionGeneratorService ai,
        EmbeddingService embeddings,
        IGooglePlacesService googlePlaces,
        ITaxonomySvc taxonomy,
        IPhotoRehostService photoRehost,
        ILogger<PlaceImportService> logger,
        TimeProvider clock)
    {
        _db = db;
        _ai = ai;
        _embeddings = embeddings;
        _googlePlaces = googlePlaces;
        _taxonomy = taxonomy;
        _photoRehost = photoRehost;
        _logger = logger;
        _clock = clock;
    }

    // ── BulkImport ────────────────────────────────────────────────────────

    public async Task<BulkImportResult> BulkImportAsync(
        List<CreatePlaceRequest> requests,
        Guid? userId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var results = new List<BulkImportItemResult>();
        int errors = 0, skipped = 0;

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

        // Validate + Name+City dedup; GooglePlaceId dedup handled inside InsertWithDedupAsync
        var validRequests = new List<CreatePlaceRequest>();

        foreach (var request in requests)
        {
            if (!PlaceTaxonomy.IsValidCategory(request.Category))
            {
                results.Add(new BulkImportItemResult(request.Name, "error", $"Invalid category: {request.Category}", null));
                errors++;
                continue;
            }

            request.Subcategories = request.Subcategories?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
            if (!await _taxonomy.AreValidSubcategoriesAsync(request.Category, request.Subcategories, ct))
            {
                results.Add(new BulkImportItemResult(request.Name, "error", $"Invalid subcategory for category '{request.Category}'", null));
                errors++;
                continue;
            }

            if (IsNameCityDuplicate(request, existingNameCities))
            {
                results.Add(new BulkImportItemResult(request.Name, "skipped_duplicate", "Name+City already exists", null));
                skipped++;
                continue;
            }
            existingNameCities.Add((request.Name.Trim().ToLowerInvariant(), (request.City?.Trim() ?? "Miami").ToLowerInvariant()));
            validRequests.Add(request);
        }

        await FillDescriptionsAsync(validRequests, "Miami", ct);

        var (created, skippedByGoogle, addedPlaces) = await InsertWithDedupAsync(validRequests, userId, now, ct);
        skipped += skippedByGoogle;

        // Build per-item results for valid requests by correlating with added places
        foreach (var req in validRequests)
        {
            if (!string.IsNullOrEmpty(req.GooglePlaceId))
            {
                var placed = addedPlaces.FirstOrDefault(p =>
                    string.Equals(p.GooglePlaceId, req.GooglePlaceId, StringComparison.OrdinalIgnoreCase));
                if (placed != null)
                    results.Add(new BulkImportItemResult(req.Name, "created", null, placed.Id));
                else
                    results.Add(new BulkImportItemResult(req.Name, "skipped_duplicate", "GooglePlaceId already exists", null));
            }
            else
            {
                var placed = addedPlaces.FirstOrDefault(p =>
                    string.Equals(p.Name, req.Name.Trim(), StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(p.City, req.City?.Trim() ?? "Miami", StringComparison.OrdinalIgnoreCase));
                results.Add(new BulkImportItemResult(req.Name, "created", null, placed?.Id));
            }
        }

        _logger.LogInformation("Bulk import: {Created} created, {Skipped} skipped, {Errors} errors", created, skipped, errors);

        return new BulkImportResult(created, skipped, errors, results);
    }

    // ── ImportFromUrls ────────────────────────────────────────────────────

    public async Task<ImportFromUrlsResponse> ImportFromUrlsAsync(
        ImportFromUrlsRequest request,
        Guid? userId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var rows = new List<ImportRowResult>();
        var toImport = new List<CreatePlaceRequest>();

        foreach (var rawUrl in request.Urls)
        {
            // Reject short links up front — Google blocks server-side resolution for goo.gl/g.co
            if (Uri.TryCreate(rawUrl, UriKind.Absolute, out var parsedUri) &&
                (parsedUri.Host.EndsWith("goo.gl", StringComparison.OrdinalIgnoreCase) ||
                 parsedUri.Host.EndsWith("g.co", StringComparison.OrdinalIgnoreCase)))
            {
                rows.Add(new ImportRowResult(rawUrl, null, null, "failed_resolve",
                    "Short links no se resuelven. Abre el link en el navegador y pega la URL canónica (google.com/maps/place/...) o el Place ID directo."));
                continue;
            }

            // Step 1 — resolve URL → Place ID
            var placeId = await _googlePlaces.ResolvePlaceIdFromUrlAsync(rawUrl, ct);
            if (placeId is null)
            {
                rows.Add(new ImportRowResult(rawUrl, null, null, "failed_resolve",
                    "Could not extract a Place ID from this URL."));
                continue;
            }

            // Step 2 — fetch full details
            var details = await _googlePlaces.GetDetailsAsync(placeId, ct);
            if (details is null)
            {
                rows.Add(new ImportRowResult(rawUrl, placeId, null, "failed_details",
                    "Google Places API returned no data for this Place ID."));
                continue;
            }

            // Step 3 — map to CreatePlaceRequest
            var category = PlaceTaxonomy.CategoryFromGoogleTypes(details.PrimaryType, details.Types);
            var validCategory = category ?? "Culture";
            var allowedSubs = (await _taxonomy.GetByCategoryAsync(validCategory, ct)).Select(s => s.Key).ToList();
            var subcategory = PlaceTaxonomy.CanonicalSubcategoryFromGoogleTypes(validCategory, details.Types, allowedSubs, details.Name);

            toImport.Add(new CreatePlaceRequest
            {
                Name = details.Name,
                Category = validCategory,
                Subcategories = subcategory != null ? new List<string> { subcategory } : null,
                WhyThisPlace = details.EditorialSummary ?? "",
                City = details.City ?? request.DefaultCity ?? "Miami",
                Neighborhood = details.Neighborhood,
                Latitude = details.Lat,
                Longitude = details.Lng,
                GooglePlaceId = details.Id,
                GoogleRating = details.Rating,
                GoogleReviewCount = details.ReviewCount,
                PriceRange = details.PriceLevel,
                Photos = details.Photos.Count > 0 ? details.Photos : null,
                Source = request.Source,
                Status = request.DefaultStatus,
                OpeningHours = details.OpeningHours,
            });

            rows.Add(new ImportRowResult(rawUrl, details.Id, details.Name, "pending", null));
        }

        if (toImport.Count == 0)
        {
            var failed = rows.Count;
            return new ImportFromUrlsResponse(0, 0, 0, failed, rows);
        }

        await FillDescriptionsAsync(toImport, request.DefaultCity ?? "Miami", ct);

        // Step 4 — bulk insert with dedup
        var (created, skipped, addedPlaces) = await InsertWithDedupAsync(toImport, userId, now, ct);

        // Update row status based on what was actually inserted
        var addedPlaceIds = addedPlaces
            .Select(p => p.GooglePlaceId)
            .Where(id => id != null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;

        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].Status != "pending") continue;
            if (rows[i].PlaceId != null && addedPlaceIds.Contains(rows[i].PlaceId!))
                rows[i] = rows[i] with { Status = "created" };
            else
                rows[i] = rows[i] with { Status = "skipped_duplicate", Error = "GooglePlaceId already in library." };
        }

        if (addedPlaces.Count > 0)
        {
            // Generate embeddings inline for newly created places
            try
            {
                var texts = addedPlaces
                    .Select(p => EmbeddingService.BuildPlaceIndexText(
                        p.Name, p.Category, p.Subcategories,
                        p.Neighborhood, p.City, p.WhyThisPlace, p.BestFor, p.SuitableFor))
                    .ToList();

                var vectors = await _embeddings.EmbedBatchAsync(texts, ct);
                if (vectors.Count == addedPlaces.Count)
                {
                    for (var i = 0; i < addedPlaces.Count; i++)
                    {
                        addedPlaces[i].Embedding = vectors[i];
                        addedPlaces[i].UpdatedAt = _clock.GetUtcNow();
                    }
                    await _db.SaveChangesAsync(ct);
                }
                else
                {
                    _logger.LogWarning("Embedding batch size mismatch: expected {E} got {G}. Embeddings skipped.",
                        addedPlaces.Count, vectors.Count);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Inline embedding generation failed after import-from-urls. Run reindex-embeddings?onlyMissing=true to recover.");
            }
        }

        var failedCount = rows.Count(r => r.Status.StartsWith("failed"));
        _logger.LogInformation("import-from-urls: resolved={Res} created={C} skipped={S} failed={F}",
            rows.Count - failedCount, created, skipped, failedCount);

        return new ImportFromUrlsResponse(rows.Count - failedCount, created, skipped, failedCount, rows);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true when the request should be skipped due to matching an existing Name+City
    /// combination. Requests with a non-null GooglePlaceId are always allowed through here —
    /// they are deduped later in InsertWithDedupAsync by GooglePlaceId.
    /// </summary>
    internal static bool IsNameCityDuplicate(CreatePlaceRequest req, HashSet<(string name, string city)> existingKeys)
    {
        if (!string.IsNullOrEmpty(req.GooglePlaceId)) return false;
        return existingKeys.Contains(
            (req.Name.Trim().ToLowerInvariant(), (req.City?.Trim() ?? "Miami").ToLowerInvariant()));
    }

    private async Task FillDescriptionsAsync(
        List<CreatePlaceRequest> requests,
        string defaultCity,
        CancellationToken ct)
    {
        foreach (var chunk in requests.Where(r => PlaceTaxonomy.IsPlaceholderOrEmpty(r.WhyThisPlace)).Chunk(5))
        {
            await Task.WhenAll(chunk.Select(async req =>
            {
                var generated = await _ai.GeneratePlaceDescriptionAsync(
                    req.Name, req.City ?? defaultCity, req.Category,
                    req.Subcategories?.FirstOrDefault(),
                    null, req.GoogleRating, req.GoogleReviewCount, req.Neighborhood, ct);
                if (generated != null) req.WhyThisPlace = generated;
            }));
        }
    }

    /// <summary>
    /// Inserts already-validated CreatePlaceRequests, deduplicating by GooglePlaceId.
    /// Name+City dedup and category validation must be done by the caller beforehand.
    /// Returns (created, skipped, newly-added Place entities) for post-processing (e.g. embeddings).
    /// </summary>
    private async Task<(int created, int skipped, List<Place> added)> InsertWithDedupAsync(
        IReadOnlyList<CreatePlaceRequest> requests,
        Guid? userId,
        DateTimeOffset now,
        CancellationToken ct)
    {
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

        var placesToAdd = new List<Place>();
        var pendingChunk = new List<Place>();
        int created = 0, skipped = 0;

        // M5: commit por chunks de 10. El rehost inline es lento (descarga+reencode+upload
        // por foto) y un import de 500 places no cabe bajo el proxy de 40s de Railway — con
        // un único SaveChanges al final, la cancelación perdía TODO lo ya rehosteado (Google
        // facturado + objetos R2 huérfanos sin fila en DB). Con chunks, lo completado antes
        // de la cancelación queda persistido y el re-run dedupa por GooglePlaceId/Name+City.
        const int CommitChunkSize = 10;

        foreach (var req in requests)
        {
            if (!string.IsNullOrEmpty(req.GooglePlaceId) && existingGoogleIds.Contains(req.GooglePlaceId))
            {
                skipped++;
                continue;
            }

            // Rehost a R2 ANTES de persistir: Place.Photos solo debe contener URLs de R2.
            // Solo se rehostean los requests que pasan dedup (no se sube nada para skips).
            var photos = await _photoRehost.RehostForIngestAsync(req.Photos, req.Name, ct);

            var place = new Place
            {
                Id = Guid.NewGuid(),
                Name = req.Name.Trim(),
                Category = req.Category,
                WhyThisPlace = req.WhyThisPlace?.Trim() ?? string.Empty,
                Subcategories = req.Subcategories,
                Neighborhood = req.Neighborhood?.Trim(),
                City = req.City?.Trim() ?? "Miami",
                Latitude = req.Latitude,
                Longitude = req.Longitude,
                BestFor = req.BestFor,
                SuitableFor = req.SuitableFor,
                BestTimes = req.BestTimes,
                PriceRange = req.PriceRange?.Trim(),
                Photos = photos,
                GooglePlaceId = req.GooglePlaceId?.Trim(),
                GoogleRating = req.GoogleRating,
                GoogleReviewCount = req.GoogleReviewCount,
                Source = req.Source?.Trim() ?? "curated",
                SourceUrl = req.SourceUrl?.Trim(),
                Status = req.Status?.Trim() ?? "in_review",
                AiVibeScore = req.AiVibeScore,
                VisitDurationMin = req.VisitDurationMin,
                OpeningHours = req.OpeningHours?.ToJsonDocument(),
                Flags = req.Flags,
                SubmittedById = userId,
                CreatedAt = now,
                UpdatedAt = now
            };

            placesToAdd.Add(place);
            pendingChunk.Add(place);
            existingGoogleIds.Add(req.GooglePlaceId ?? "");
            created++;

            if (pendingChunk.Count >= CommitChunkSize)
            {
                _db.Places.AddRange(pendingChunk);
                await _db.SaveChangesAsync(ct);
                pendingChunk.Clear();
            }
        }

        if (pendingChunk.Count > 0)
        {
            _db.Places.AddRange(pendingChunk);
            await _db.SaveChangesAsync(ct);
        }

        return (created, skipped, placesToAdd);
    }
}
