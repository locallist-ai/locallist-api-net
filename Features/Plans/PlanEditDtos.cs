using System.ComponentModel.DataAnnotations;

namespace LocalList.API.NET.Features.Plans;

public class UpdateStopsRequest
{
    [Required]
    [MinLength(1)]
    public List<StopInput> Stops { get; set; } = [];
}

public class StopInput
{
    [Required]
    public Guid PlaceId { get; set; }

    [Required]
    [Range(1, 7)]
    public int DayNumber { get; set; }

    [Required]
    [Range(0, 50)]
    public int OrderIndex { get; set; }

    [StringLength(20)]
    public string? TimeBlock { get; set; }

    [Range(1, 480)]
    public int? SuggestedDurationMin { get; set; }
}

public class CreateUserPlanRequest
{
    [Required, StringLength(255)]
    public required string Name { get; set; }

    [Required, StringLength(100)]
    public required string City { get; set; }

    [StringLength(20)]
    public string? Type { get; set; }

    [Range(1, 7)]
    public int DurationDays { get; set; } = 1;
}
