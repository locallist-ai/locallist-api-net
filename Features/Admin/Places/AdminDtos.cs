using System.ComponentModel.DataAnnotations;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.NET.Features.Admin.Places;

public record AdminPlaceDto(
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
    string? RejectionReason,
    int? AiVibeScore,
    List<string>? Flags,
    Guid? SubmittedById,
    Guid? ReviewedById,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
)
{
    public static AdminPlaceDto FromEntity(Place p) => new(
        p.Id, p.Name, p.Category, p.Subcategory, p.Neighborhood, p.City,
        p.Latitude, p.Longitude, p.WhyThisPlace, p.BestFor, p.SuitableFor,
        p.BestTime, p.PriceRange, p.Photos, p.GooglePlaceId, p.GoogleRating,
        p.GoogleReviewCount, p.Source, p.SourceUrl, p.Status,
        p.RejectionReason, p.AiVibeScore, p.Flags,
        p.SubmittedById, p.ReviewedById, p.CreatedAt, p.UpdatedAt
    );
}

public class CreatePlaceRequest
{
    [Required, StringLength(255, MinimumLength = 1)]
    public required string Name { get; set; }

    [Required, StringLength(50)]
    public required string Category { get; set; }

    [Required, MinLength(1)]
    public required string WhyThisPlace { get; set; }

    [StringLength(100)]
    public string? Subcategory { get; set; }

    [StringLength(100)]
    public string? Neighborhood { get; set; }

    [StringLength(100)]
    public string? City { get; set; }

    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public List<string>? BestFor { get; set; }
    public List<string>? SuitableFor { get; set; }

    [StringLength(50)]
    public string? BestTime { get; set; }

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
    public List<string>? Flags { get; set; }
}

public class ReviewPlaceRequest
{
    [Required, RegularExpression("^(published|rejected)$", ErrorMessage = "Status must be 'published' or 'rejected'.")]
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

    [StringLength(100)]
    public string? Subcategory { get; set; }

    [StringLength(100)]
    public string? Neighborhood { get; set; }

    [StringLength(100)]
    public string? City { get; set; }

    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public List<string>? BestFor { get; set; }
    public List<string>? SuitableFor { get; set; }

    [StringLength(50)]
    public string? BestTime { get; set; }

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
    public List<string>? Flags { get; set; }
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
