using LocalList.API.NET.Shared.Data.Entities;
using LocalList.API.NET.Shared.I18n;

namespace LocalList.API.NET.Features.Places;

/// <summary>
/// Public-facing Place DTO. Excludes internal curation fields
/// (rejection_reason, ai_vibe_score, flags, submitted_by, reviewed_by).
/// </summary>
public record PlaceDto(
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
    string? BestTime,
    string? PriceRange,
    List<string>? Photos,
    string? GooglePlaceId,
    decimal? GoogleRating,
    int? GoogleReviewCount,
    string Source,
    string? SourceUrl,
    string Status,
    DateTimeOffset CreatedAt,
    OpeningHoursData? OpeningHours = null
)
{
    // Adapter for legacy app clients that still read the singular field.
    public string? Subcategory => Subcategories?.FirstOrDefault();

    public static PlaceDto FromEntity(Place p, string lang = "en")
    {
        var isCurated = p.Source == "curated";
        var ts = p.TranslationStatus;

        // Resolve canonical subcategories with i18n: prefer new array fields, fall back to legacy.
        var subsI18n = p.SubcategoriesI18n != null
            ? LanguageAccessor.ResolveStringList(p.SubcategoriesI18n, lang, null, isCurated, ts)
            : null;
        var subs = subsI18n
            ?? (p.Subcategories is { Count: > 0 } ? p.Subcategories : null)
            ?? (LanguageAccessor.ResolveString(p.SubcategoryI18n, lang, p.Subcategory, isCurated, ts) is string legacySub
                ? new List<string> { legacySub } : null);

        return new(
            p.Id,
            LanguageAccessor.ResolveString(p.NameI18n, lang, p.Name, isCurated, ts) ?? p.Name,
            p.Category,
            subs,
            LanguageAccessor.ResolveString(p.NeighborhoodI18n, lang, p.Neighborhood, isCurated, ts),
            p.City,
            p.Latitude,
            p.Longitude,
            LanguageAccessor.ResolveString(p.WhyThisPlaceI18n, lang, p.WhyThisPlace, isCurated, ts) ?? p.WhyThisPlace,
            LanguageAccessor.ResolveStringList(p.BestForI18n, lang, p.BestFor, isCurated, ts),
            LanguageAccessor.ResolveStringList(p.SuitableForI18n, lang, p.SuitableFor, isCurated, ts),
            LanguageAccessor.ResolveString(p.BestTimeI18n, lang, p.BestTime, isCurated, ts),
            p.PriceRange,
            p.Photos,
            p.GooglePlaceId,
            p.GoogleRating,
            p.GoogleReviewCount,
            p.Source,
            p.SourceUrl,
            p.Status,
            p.CreatedAt,
            OpeningHours: OpeningHoursData.FromJsonDocument(p.OpeningHours)
        );
    }
}
