using System.Text.Json;
using LocalList.API.NET.Features.Places;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.NET.Features.Plans;

public record PlanDto(
    Guid Id,
    string Name,
    string City,
    string Type,
    string? Description,
    string? ImageUrl,
    int DurationDays,
    JsonDocument? TripContext,
    bool IsPublic,
    bool IsShowcase,
    Guid? CreatedById,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
)
{
    public static PlanDto FromEntity(Plan p) => new(
        p.Id, p.Name, p.City, p.Type, p.Description, p.ImageUrl,
        p.DurationDays, p.TripContext, p.IsPublic, p.IsShowcase,
        p.CreatedById, p.CreatedAt, p.UpdatedAt
    );
}

public record PlanStopResponseDto(
    Guid Id,
    Guid PlaceId,
    int DayNumber,
    int OrderIndex,
    string? TimeBlock,
    TimeSpan? SuggestedArrival,
    int? SuggestedDurationMin,
    JsonDocument? TravelFromPrevious,
    PlaceDto? Place
)
{
    public static PlanStopResponseDto FromEntity(PlanStop s) => new(
        s.Id, s.PlaceId, s.DayNumber, s.OrderIndex,
        s.TimeBlock, s.SuggestedArrival, s.SuggestedDurationMin,
        s.TravelFromPrevious,
        s.Place is null ? null : PlaceDto.FromEntity(s.Place)
    );
}

public record PlanDayDto(int DayNumber, List<PlanStopResponseDto> Stops);

public record PlanDetailDto(
    Guid Id,
    string Name,
    string City,
    string Type,
    string? Description,
    string? ImageUrl,
    int DurationDays,
    JsonDocument? TripContext,
    bool IsPublic,
    bool IsShowcase,
    Guid? CreatedById,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    List<PlanDayDto> Days
)
{
    public static PlanDetailDto FromEntity(Plan plan)
    {
        var days = plan.Stops
            .OrderBy(s => s.DayNumber)
            .ThenBy(s => s.OrderIndex)
            .GroupBy(s => s.DayNumber)
            .Select(g => new PlanDayDto(
                g.Key,
                g.Select(PlanStopResponseDto.FromEntity).ToList()
            ))
            .ToList();
        return Build(plan, days);
    }

    public static PlanDetailDto FromEntityWithAllDays(Plan plan)
    {
        var stopsByDay = plan.Stops
            .GroupBy(s => s.DayNumber)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(s => s.OrderIndex)
                    .Select(PlanStopResponseDto.FromEntity)
                    .ToList());

        var days = Enumerable.Range(1, plan.DurationDays)
            .Select(d => new PlanDayDto(
                d,
                stopsByDay.TryGetValue(d, out var s) ? s : []
            ))
            .ToList();
        return Build(plan, days);
    }

    private static PlanDetailDto Build(Plan p, List<PlanDayDto> days) => new(
        p.Id, p.Name, p.City, p.Type, p.Description, p.ImageUrl,
        p.DurationDays, p.TripContext, p.IsPublic, p.IsShowcase,
        p.CreatedById, p.CreatedAt, p.UpdatedAt, days
    );
}

public record PlansListResponse(List<PlanDto> Plans, int Total);
