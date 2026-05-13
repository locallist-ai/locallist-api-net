using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace LocalList.API.NET.Shared.Data.Entities;

[Table("user_profiles")]
public class UserProfile
{
    [Key]
    [Column("user_id")]
    public Guid UserId { get; set; }

    [Column("default_group_type")]
    [StringLength(20)]
    public string? DefaultGroupType { get; set; }   // couple | family | solo | friends

    [Column("companion_tags")]
    public List<string> CompanionTags { get; set; } = new(); // with-kids | honeymoon | ...

    [Column("dietary_restrictions")]
    public List<string> DietaryRestrictions { get; set; } = new(); // vegetarian | vegan | halal | ...

    [Column("pace_preference")]
    [StringLength(10)]
    public string? PacePreference { get; set; }     // slow | normal | fast

    [Column("default_budget_tier")]
    [StringLength(20)]
    public string? DefaultBudgetTier { get; set; }  // budget | moderate | premium

    [Column("favorite_city")]
    [StringLength(100)]
    public string? FavoriteCity { get; set; }

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    // Navigation
    public User? User { get; set; }
}
