using System.ComponentModel.DataAnnotations;

namespace LocalList.API.NET.Features.Profile;

public class UpsertProfileRequest
{
    [MaxLength(20)]
    public string? DefaultGroupType { get; set; }

    [MaxLength(10)]
    public List<string>? CompanionTags { get; set; }

    [MaxLength(10)]
    public List<string>? DietaryRestrictions { get; set; }

    [MaxLength(10)]
    public string? PacePreference { get; set; }

    [MaxLength(20)]
    public string? DefaultBudgetTier { get; set; }

    [MaxLength(100)]
    public string? FavoriteCity { get; set; }
}

public class ProfileResponse
{
    public string? DefaultGroupType { get; set; }
    public List<string> CompanionTags { get; set; } = new();
    public List<string> DietaryRestrictions { get; set; } = new();
    public string? PacePreference { get; set; }
    public string? DefaultBudgetTier { get; set; }
    public string? FavoriteCity { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
