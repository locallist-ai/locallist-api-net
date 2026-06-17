namespace LocalList.API.NET.Shared.Dtos;

public class ScheduledStopDto
{
    public Guid PlaceId { get; set; }
    public int DayNumber { get; set; }
    public int OrderIndex { get; set; }
    public string TimeBlock { get; set; } = string.Empty;
    public string? SuggestedArrival { get; set; }
    public int SuggestedDurationMin { get; set; }
    public TravelInfoDto? TravelFromPrevious { get; set; }
}

public class TravelInfoDto
{
    public double distance_km { get; set; }
    public int duration_min { get; set; }
    public string mode { get; set; } = "drive";
}

public sealed class ScheduleResult
{
    public List<ScheduledStopDto> Stops { get; } = new();
    public List<string> Warnings { get; } = new();
    public List<string> AppliedRefinements { get; } = new();
}
