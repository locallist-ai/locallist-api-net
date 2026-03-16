using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using LocalList.API.NET.Shared.Auth;
using LocalList.API.NET.Shared.Data;
using LocalList.API.NET.Shared.Data.Entities;

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

    private static readonly HashSet<string> ValidCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Food", "Nightlife", "Coffee", "Outdoors", "Wellness", "Culture"
    };

    private static readonly HashSet<string> ValidStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "draft", "in_review", "published", "rejected"
    };

    public AdminPlacesController(LocalListDbContext db, ILogger<AdminPlacesController> logger, TimeProvider clock)
    {
        _db = db;
        _logger = logger;
        _clock = clock;
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
        if (!ValidCategories.Contains(request.Category))
            return BadRequest(new { error = $"Invalid category. Valid: {string.Join(", ", ValidCategories)}" });

        var userId = GetUserId();
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

        var userId = GetUserId();
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
            if (!ValidCategories.Contains(request.Category))
            {
                results.Add(new BulkImportItemResult(request.Name, "error", $"Invalid category: {request.Category}", null));
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

        if (request.Category != null && !ValidCategories.Contains(request.Category))
            return BadRequest(new { error = $"Invalid category. Valid: {string.Join(", ", ValidCategories)}" });

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
        place.ReviewedById = GetUserId();
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

    private Guid? GetUserId()
    {
        var sub = User.FindFirstValue(JwtRegisteredClaimNames.Sub)
                  ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}
