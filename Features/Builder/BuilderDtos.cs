using System.ComponentModel.DataAnnotations;

namespace LocalList.API.NET.Features.Builder;

public class BuilderChatRequest
{
    [Required]
    [MinLength(1)]
    [MaxLength(5000)]
    public required string Message { get; set; }
    public TripContextDto? TripContext { get; set; }
}

public class ExtractedPreferences
{
    public int Days { get; set; } = 1;
    public List<string> Categories { get; set; } = new();
    public List<string> Vibes { get; set; } = new();
    public string GroupType { get; set; } = "couple";
    public string PlanName { get; set; } = "My Plan";
    public int MaxStopsPerDay { get; set; } = 5;
}

public class TripContextDto
{
    [MaxLength(20)]
    public string? GroupType { get; set; }
    [MaxLength(10)]
    public List<string>? Preferences { get; set; }
    [MaxLength(10)]
    public List<string>? Vibes { get; set; }
    [Range(1, 7)]
    public int? Days { get; set; }
    [MaxLength(100)]
    public string? City { get; set; }
}
