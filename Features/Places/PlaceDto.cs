using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.NET.Features.Places;

/// <summary>
/// Public-facing Place DTO. Excludes internal curation fields
/// (rejection_reason, ai_vibe_score, flags, submitted_by, reviewed_by).
/// </summary>
public record PlaceDto(
    Guid Id,
    string Name,
    string Category,
    string? Subcategory,
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
    DateTimeOffset CreatedAt
)
{
    public static PlaceDto FromEntity(Place p) => new(
        p.Id,
        p.Name,
        p.Category,
        p.Subcategory,
        p.Neighborhood,
        p.City,
        p.Latitude,
        p.Longitude,
        p.WhyThisPlace,
        p.BestFor,
        p.SuitableFor,
        p.BestTime,
        p.PriceRange,
        p.Photos,
        p.GooglePlaceId,
        p.GoogleRating,
        p.GoogleReviewCount,
        p.Source,
        p.SourceUrl,
        p.Status,
        p.CreatedAt
    );
}
