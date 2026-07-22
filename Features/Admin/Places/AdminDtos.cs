using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using LocalList.API.NET.Shared.Dtos;
using LocalList.API.NET.Shared.Data.Entities;
using LocalList.API.NET.Shared.I18n;

namespace LocalList.API.NET.Features.Admin.Places;

public record AdminPlaceDto(
    Guid Id,
    string Name,
    string Category,
    List<string>? Subcategories,
    string? Neighborhood,
    string City,
    decimal? Latitude,
    decimal? Longitude,
    string WhyThisPlace,
    List<string>? BestFor,
    List<string>? SuitableFor,
    List<string>? BestTimes,
    string? PriceRange,
    List<string>? Photos,
    string? GooglePlaceId,
    decimal? GoogleRating,
    int? GoogleReviewCount,
    string Source,
    string? SourceUrl,
    string Status,
    string? RejectionReason,
    int? AiVibeScore,
    int? VisitDurationMin,
    List<string>? Flags,
    Guid? SubmittedById,
    Guid? ReviewedById,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    // i18n fields — ES draft/approved content
    string? NameEs,
    string? WhyThisPlaceEs,
    List<string>? BestTimesEs,
    string? NeighborhoodEs,
    List<string>? SubcategoriesEs,
    List<string>? BestForEs,
    List<string>? SuitableForEs,
    string? TranslationStatusEs,
    OpeningHoursData? OpeningHours = null
)
{
    public static AdminPlaceDto FromEntity(Place p)
    {
        var subs = p.Subcategories is { Count: > 0 } ? p.Subcategories : null;
        var subsEs = LanguageAccessor.ResolveStringList(p.SubcategoriesI18n, "es", p.Subcategories, isCurated: false);
        var bestTimes = p.BestTimes is { Count: > 0 } ? p.BestTimes : null;

        return new(
            p.Id, p.Name, p.Category, subs, p.Neighborhood, p.City,
            p.Latitude, p.Longitude, p.WhyThisPlace, p.BestFor, p.SuitableFor,
            bestTimes, p.PriceRange, p.Photos, p.GooglePlaceId, p.GoogleRating,
            p.GoogleReviewCount, p.Source, p.SourceUrl, p.Status,
            p.RejectionReason, p.AiVibeScore, p.VisitDurationMin, p.Flags,
            p.SubmittedById, p.ReviewedById, p.CreatedAt, p.UpdatedAt,
            NameEs: LanguageAccessor.ResolveString(p.NameI18n, "es", null, isCurated: false),
            WhyThisPlaceEs: LanguageAccessor.ResolveString(p.WhyThisPlaceI18n, "es", null, isCurated: false),
            BestTimesEs: LanguageAccessor.ResolveStringList(p.BestTimesI18n, "es", null, isCurated: false),
            NeighborhoodEs: LanguageAccessor.ResolveString(p.NeighborhoodI18n, "es", null, isCurated: false),
            SubcategoriesEs: subsEs,
            BestForEs: LanguageAccessor.ResolveStringList(p.BestForI18n, "es", null, isCurated: false),
            SuitableForEs: LanguageAccessor.ResolveStringList(p.SuitableForI18n, "es", null, isCurated: false),
            TranslationStatusEs: p.TranslationStatus?.RootElement.TryGetProperty("es", out var tsEs) == true
                && tsEs.ValueKind == JsonValueKind.String ? tsEs.GetString() : null,
            OpeningHours: OpeningHoursData.FromJsonDocument(p.OpeningHours)
        );
    }
}

public class CreatePlaceRequest
{
    [Required, StringLength(255, MinimumLength = 1)]
    public required string Name { get; set; }

    [Required, StringLength(50)]
    public required string Category { get; set; }

    public string? WhyThisPlace { get; set; }

    public List<string>? Subcategories { get; set; }

    [StringLength(100)]
    public string? Neighborhood { get; set; }

    [StringLength(100)]
    public string? City { get; set; }

    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public List<string>? BestFor { get; set; }
    public List<string>? SuitableFor { get; set; }

    public List<string>? BestTimes { get; set; }

    [StringLength(10)]
    public string? PriceRange { get; set; }

    public List<string>? Photos { get; set; }

    [StringLength(255)]
    public string? GooglePlaceId { get; set; }

    public decimal? GoogleRating { get; set; }
    public int? GoogleReviewCount { get; set; }

    [StringLength(50)]
    public string? Source { get; set; }

    public string? SourceUrl { get; set; }

    [StringLength(20)]
    public string? Status { get; set; }

    public int? AiVibeScore { get; set; }
    public int? VisitDurationMin { get; set; }
    public List<string>? Flags { get; set; }
    public OpeningHoursData? OpeningHours { get; set; }
}

public class ReviewPlaceRequest
{
    [Required, RegularExpression("^(published|rejected|in_review)$", ErrorMessage = "Status must be 'published', 'rejected', or 'in_review'.")]
    public required string Status { get; set; }

    [StringLength(1000)]
    public string? RejectionReason { get; set; }
}

public class UpdatePlaceRequest
{
    [StringLength(255)]
    public string? Name { get; set; }

    [StringLength(50)]
    public string? Category { get; set; }

    public string? WhyThisPlace { get; set; }

    public List<string>? Subcategories { get; set; }

    [StringLength(100)]
    public string? Neighborhood { get; set; }

    [StringLength(100)]
    public string? City { get; set; }

    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public List<string>? BestFor { get; set; }
    public List<string>? SuitableFor { get; set; }

    public List<string>? BestTimes { get; set; }

    [StringLength(10)]
    public string? PriceRange { get; set; }

    public List<string>? Photos { get; set; }

    [StringLength(255)]
    public string? GooglePlaceId { get; set; }

    public decimal? GoogleRating { get; set; }
    public int? GoogleReviewCount { get; set; }

    [StringLength(50)]
    public string? Source { get; set; }

    public string? SourceUrl { get; set; }

    public int? AiVibeScore { get; set; }
    public int? VisitDurationMin { get; set; }
    public List<string>? Flags { get; set; }

    // i18n ES fields — null means "no change", empty string clears the field
    public string? NameEs { get; set; }
    public string? WhyThisPlaceEs { get; set; }
    public List<string>? BestTimesEs { get; set; }
    public string? NeighborhoodEs { get; set; }
    public List<string>? SubcategoriesEs { get; set; }
    public List<string>? BestForEs { get; set; }
    public List<string>? SuitableForEs { get; set; }
    // "draft" | "approved" | null (null = no change)
    public string? TranslationStatusEs { get; set; }
}

public record BulkImportResult(
    int Created,
    int Skipped,
    int Errors,
    List<BulkImportItemResult> Items
);

public record BulkImportItemResult(
    string Name,
    string Result,
    string? Error,
    Guid? Id
);

public class ImportFromUrlsRequest
{
    [Required]
    [MaxLength(500, ErrorMessage = "Maximum 500 URLs per request.")]
    public List<string> Urls { get; set; } = new();

    [StringLength(100)]
    public string? DefaultCity { get; set; }

    [RegularExpression("^(draft|in_review|published|rejected)$")]
    public string DefaultStatus { get; set; } = "in_review";

    [StringLength(50)]
    public string Source { get; set; } = "curated";
}

public record ImportFromUrlsResponse(
    int Resolved,
    int Created,
    int Skipped,
    int Failed,
    List<ImportRowResult> Rows
);

public record ImportRowResult(
    string Input,
    string? PlaceId,
    string? Name,
    string Status,   // created | skipped_duplicate | failed_resolve | failed_details
    string? Error
);

/// <summary>Fila del censo por dominio de POST /admin/places/backfill-photos.</summary>
/// <param name="Photos">Fotos persistidas en este dominio ANTES de este run (censo global).</param>
/// <param name="Migrated">Fotos de este dominio migradas a R2 en este run.</param>
/// <param name="Failed">Fotos de este dominio cuyo rehost falló en este run (conservan la URL original).</param>
public record PhotoDomainCensus(int Photos, int Migrated, int Failed);

/// <summary>
/// Respuesta de POST /admin/places/backfill-photos. <c>Census</c> siempre incluye los
/// cuatro buckets (places.googleapis.com / r2.dev / wanderlog.com / other) aunque estén
/// a cero; <c>OtherDomains</c> desglosa por host real las fotos del bucket "other".
/// </summary>
public record BackfillPhotosResponse(
    bool R2Configured,
    bool DryRun,
    int TotalPlacesWithPhotos,
    int CandidatePlaces,
    int ProcessedPlaces,
    int UpdatedPlaces,
    int RemainingPlaces,
    Dictionary<string, PhotoDomainCensus> Census,
    Dictionary<string, int> OtherDomains
);

public class GoogleSearchRequest
{
    [Required, StringLength(200, MinimumLength = 1)]
    public required string Query { get; set; }

    [StringLength(100)]
    public string City { get; set; } = "Miami";
}

public record GooglePlacePreview(
    string GooglePlaceId,
    string Name,
    string? FormattedAddress,
    decimal? Lat,
    decimal? Lng,
    decimal? Rating,
    int? ReviewCount,
    string? PriceLevel,
    List<string> Photos,
    List<string> Types,
    string? Website,
    string? Phone,
    string? EditorialSummary,
    bool ExistsInLib
);
