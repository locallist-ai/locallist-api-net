namespace LocalList.API.NET.Shared.Constants;

public static class PlanLimits
{
    public const int MaxStopsPerDay = 10;

    /// <summary>
    /// Hard cap on plan duration (days) for EVERYONE — this is the Plus ceiling and the
    /// global maximum any plan can span. Free tier is capped lower at runtime
    /// (<c>PlanGenerationGateService.FreeMaxDays</c>); this constant is the single source of
    /// truth for every <c>DayNumber</c>/<c>DurationDays</c> <c>[Range]</c> validation across the
    /// edit + admin DTOs and for <c>PlanGenerationGateService.PlusMaxDays</c>, so the DTO
    /// validation and the gate can never desync (the 7→14 drift that let a Plus generate a
    /// 14-day plan it then couldn't edit).
    /// </summary>
    public const int MaxPlanDurationDays = 14;
}
