using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

namespace LocalList.API.NET.Data.Models;

[Table("places")]
public class Place
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("name")]
    [StringLength(255)]
    [Required]
    public string Name { get; set; } = string.Empty;

    [Column("category")]
    [StringLength(50)]
    [Required]
    public string Category { get; set; } = string.Empty;

    [Column("subcategory")]
    [StringLength(100)]
    public string? Subcategory { get; set; }

    [Column("neighborhood")]
    [StringLength(100)]
    public string? Neighborhood { get; set; }

    [Column("city")]
    [StringLength(100)]
    public string City { get; set; } = "Miami";

    [Column("latitude", TypeName = "decimal(10, 7)")]
    public decimal? Latitude { get; set; }

    [Column("longitude", TypeName = "decimal(10, 7)")]
    public decimal? Longitude { get; set; }

    [Column("why_this_place")]
    [Required]
    public string WhyThisPlace { get; set; } = string.Empty;

    [Column("best_for")]
    public List<string>? BestFor { get; set; }

    [Column("suitable_for")]
    public List<string>? SuitableFor { get; set; }

    [Column("best_time")]
    [StringLength(50)]
    public string? BestTime { get; set; }

    [Column("price_range")]
    [StringLength(10)]
    public string? PriceRange { get; set; }

    [Column("photos")]
    public List<string>? Photos { get; set; }

    [Column("opening_hours", TypeName = "jsonb")]
    public JsonDocument? OpeningHours { get; set; }

    [Column("google_place_id")]
    [StringLength(255)]
    public string? GooglePlaceId { get; set; }

    [Column("google_rating", TypeName = "decimal(2, 1)")]
    public decimal? GoogleRating { get; set; }

    [Column("google_review_count")]
    public int? GoogleReviewCount { get; set; }

    [Column("source")]
    [StringLength(50)]
    public string Source { get; set; } = "curated";

    [Column("source_url")]
    public string? SourceUrl { get; set; }

    [Column("status")]
    [StringLength(20)]
    public string Status { get; set; } = "draft";

    // --- New Curation Fields for the Data Ingestion Pipeline ---
    [NotMapped]
    [Column("rejection_reason")]
    public string? RejectionReason { get; set; }

    [NotMapped]
    [Column("ai_vibe_score")]
    public int? AiVibeScore { get; set; }

    [NotMapped]
    [Column("flags")]
    public List<string>? Flags { get; set; }
    // ------------------------------------------------------------

    [Column("submitted_by")]
    public Guid? SubmittedById { get; set; }

    [Column("reviewed_by")]
    public Guid? ReviewedById { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public User? SubmittedBy { get; set; }
    public User? ReviewedBy { get; set; }
    public ICollection<PlanStop> PlanStops { get; set; } = new List<PlanStop>();
}
