using System.Text.Json;
using LocalList.API.NET.Shared.Dtos;
using LocalList.API.NET.Shared.Data.Entities;
using LocalList.API.NET.Shared.I18n;

namespace LocalList.API.NET.Features.Plans;

public record PlanDto(
    Guid Id,
    string Name,
    string City,
    string Type,
    string? Description,
    string? ImageUrl,
    int DurationDays,
    // API-3: fecha de inicio persistida (yyyy-MM-dd). null = plan sin fecha. La app la usa
    // para mostrar la fecha del viaje y derivar la fecha de cada dia (dia N = StartDate + N-1).
    DateOnly? StartDate,
    JsonDocument? TripContext,
    bool IsPublic,
    bool IsShowcase,
    Guid? CreatedById,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
)
{
    public static PlanDto FromEntity(Plan p, string lang = "en")
    {
        var isCurated = p.Source == "curated";
        return new(
            p.Id,
            LanguageAccessor.ResolveString(p.NameI18n, lang, p.Name, isCurated) ?? p.Name,
            p.City, p.Type,
            LanguageAccessor.ResolveString(p.DescriptionI18n, lang, p.Description, isCurated),
            p.ImageUrl, p.DurationDays, p.StartDate, p.TripContext, p.IsPublic, p.IsShowcase,
            p.CreatedById, p.CreatedAt, p.UpdatedAt
        );
    }
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
    public static PlanStopResponseDto FromEntity(PlanStop s, string lang = "en", string? publicBaseUrl = null) => new(
        s.Id, s.PlaceId, s.DayNumber, s.OrderIndex,
        s.TimeBlock, s.SuggestedArrival, s.SuggestedDurationMin,
        s.TravelFromPrevious,
        s.Place is null ? null : PlaceDto.FromEntity(s.Place, lang, publicBaseUrl)
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
    // API-3: fecha de inicio persistida (yyyy-MM-dd). null = plan sin fecha.
    DateOnly? StartDate,
    JsonDocument? TripContext,
    bool IsPublic,
    bool IsShowcase,
    Guid? CreatedById,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    List<PlanDayDto> Days,
    List<PlanRouteSegmentDto>? RouteSegments = null
)
{
    public static PlanDetailDto FromEntity(
        Plan plan, string lang = "en", IReadOnlyList<PlanRouteSegmentDto>? routeSegments = null, string? publicBaseUrl = null)
    {
        var days = plan.Stops
            .OrderBy(s => s.DayNumber)
            .ThenBy(s => s.OrderIndex)
            .GroupBy(s => s.DayNumber)
            .Select(g => new PlanDayDto(
                g.Key,
                g.Select(s => PlanStopResponseDto.FromEntity(s, lang, publicBaseUrl)).ToList()
            ))
            .ToList();
        return Build(plan, lang, days, routeSegments);
    }

    public static PlanDetailDto FromEntityWithAllDays(Plan plan, string lang = "en", string? publicBaseUrl = null)
    {
        var stopsByDay = plan.Stops
            .GroupBy(s => s.DayNumber)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(s => s.OrderIndex)
                    .Select(s => PlanStopResponseDto.FromEntity(s, lang, publicBaseUrl))
                    .ToList());

        var days = Enumerable.Range(1, plan.DurationDays)
            .Select(d => new PlanDayDto(
                d,
                stopsByDay.TryGetValue(d, out var s) ? s : []
            ))
            .ToList();
        return Build(plan, lang, days, null);
    }

    private static PlanDetailDto Build(Plan p, string lang, List<PlanDayDto> days, IReadOnlyList<PlanRouteSegmentDto>? routeSegments)
    {
        var isCurated = p.Source == "curated";
        return new(
            p.Id,
            LanguageAccessor.ResolveString(p.NameI18n, lang, p.Name, isCurated) ?? p.Name,
            p.City, p.Type,
            LanguageAccessor.ResolveString(p.DescriptionI18n, lang, p.Description, isCurated),
            p.ImageUrl, p.DurationDays, p.StartDate, p.TripContext, p.IsPublic, p.IsShowcase,
            p.CreatedById, p.CreatedAt, p.UpdatedAt, days,
            routeSegments?.Count > 0 ? routeSegments.ToList() : null
        );
    }
}

public record PlansListResponse(List<PlanDto> Plans, int Total);
