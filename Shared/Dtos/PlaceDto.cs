using LocalList.API.NET.Shared.Data.Entities;
using LocalList.API.NET.Shared.I18n;

namespace LocalList.API.NET.Shared.Dtos;

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
    List<string>? BestTimes,
    // deprecated: primer best time, compat app
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
    OpeningHoursData? OpeningHours = null,
    // "google" (proxy sintetizado, ver PlacePhotoUrls) | "external" (URL directa no-Google)
    // | null (sin fotos). Aditivo: la app lo usa para atribución/UX de la foto.
    string? PhotoSource = null
)
{
    /// <param name="publicBaseUrl">
    /// <c>Api:PublicBaseUrl</c> desde config. Null/vacío -> URL de proxy relativa
    /// (ver <see cref="PlacePhotoUrls.Hero"/>).
    /// </param>
    public static PlaceDto FromEntity(Place p, string lang = "en", string? publicBaseUrl = null)
    {
        var isCurated = p.Source == "curated";
        var ts = p.TranslationStatus;

        var subs = p.SubcategoriesI18n != null
            ? LanguageAccessor.ResolveStringList(p.SubcategoriesI18n, lang, p.Subcategories, isCurated, ts)
            : (p.Subcategories is { Count: > 0 } ? p.Subcategories : null);

        var bestTimes = LanguageAccessor.ResolveStringList(p.BestTimesI18n, lang, p.BestTimes, isCurated, ts);

        // Nunca reemitir una URL places.googleapis.com (con key) guardada en p.Photos: si hay
        // GooglePlaceId, se sintetiza el proxy; si no, solo pasan URLs externas no-Google.
        var (photos, photoSource) = PlacePhotoUrls.Resolve(p.Id, p.GooglePlaceId, p.Photos, publicBaseUrl);

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
            bestTimes,
            bestTimes?.FirstOrDefault(),
            p.PriceRange,
            photos,
            p.GooglePlaceId,
            p.GoogleRating,
            p.GoogleReviewCount,
            p.Source,
            p.SourceUrl,
            p.Status,
            p.CreatedAt,
            OpeningHours: OpeningHoursData.FromJsonDocument(p.OpeningHours),
            PhotoSource: photoSource
        );
    }
}
