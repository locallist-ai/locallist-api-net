using System.ComponentModel.DataAnnotations;

namespace LocalList.API.NET.Features.Admin.Plans;

public class CreatePlanRequest
{
    [Required, StringLength(255)]
    public required string Name { get; set; }

    [StringLength(100)]
    public string? City { get; set; }

    [Required, StringLength(20)]
    public required string Type { get; set; }

    public string? Description { get; set; }
    public string? ImageUrl { get; set; }

    [Range(1, 7)]
    public int DurationDays { get; set; } = 1;

    public bool IsPublic { get; set; } = true;
    public bool IsShowcase { get; set; } = false;

    public List<CreatePlanStopRequest> Stops { get; set; } = [];
}

public class CreatePlanStopRequest
{
    /// <summary>Place ID (use this OR PlaceName to resolve)</summary>
    public Guid? PlaceId { get; set; }

    /// <summary>Place name — resolved to PlaceId server-side if PlaceId is null</summary>
    public string? PlaceName { get; set; }

    [Range(1, 7)]
    public int DayNumber { get; set; } = 1;

    public int OrderIndex { get; set; }

    [StringLength(20)]
    public string? TimeBlock { get; set; }

    public TimeSpan? SuggestedArrival { get; set; }
    public int? SuggestedDurationMin { get; set; }
}
