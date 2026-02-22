using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

namespace LocalList.API.NET.Data.Models;

[Table("plans")]
public class Plan
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("name")]
    [StringLength(255)]
    [Required]
    public string Name { get; set; } = string.Empty;

    [Column("city")]
    [StringLength(100)]
    public string City { get; set; } = "Miami";

    [Column("type")]
    [StringLength(20)]
    [Required]
    public string Type { get; set; } = string.Empty;

    [Column("description")]
    public string? Description { get; set; }

    [Column("image_url")]
    public string? ImageUrl { get; set; }

    [Column("duration_days")]
    public int DurationDays { get; set; } = 1;

    [Column("trip_context", TypeName = "jsonb")]
    public JsonDocument? TripContext { get; set; }

    [Column("is_public")]
    public bool IsPublic { get; set; } = true;

    [Column("is_showcase")]
    public bool IsShowcase { get; set; } = false;

    [Column("created_by")]
    public Guid? CreatedById { get; set; }

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public User? CreatedBy { get; set; }
    public ICollection<PlanStop> Stops { get; set; } = new List<PlanStop>();
    public ICollection<FollowSession> FollowSessions { get; set; } = new List<FollowSession>();
}
