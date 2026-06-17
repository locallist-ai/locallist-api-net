using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using LocalList.API.NET.Features.Admin.Places;
using LocalList.API.NET.Shared.Data.Entities;
using LocalList.API.NET.Shared.I18n;

namespace LocalList.API.NET.Features.Admin.Plans;

public record AdminPlanDto(
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
    string Source,
    Guid? CreatedById,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? NameEs,
    string? DescriptionEs,
    string? TranslationStatusEs
)
{
    public static AdminPlanDto FromEntity(Plan p) => new(
        p.Id, p.Name, p.City, p.Type, p.Description, p.ImageUrl,
        p.DurationDays, p.TripContext, p.IsPublic, p.IsShowcase,
        p.Source, p.CreatedById, p.CreatedAt, p.UpdatedAt,
        NameEs: LanguageAccessor.ResolveString(p.NameI18n, "es", null, isCurated: false),
        DescriptionEs: LanguageAccessor.ResolveString(p.DescriptionI18n, "es", null, isCurated: false),
        TranslationStatusEs: p.TranslationStatus?.RootElement.TryGetProperty("es", out var tsEs) == true
            && tsEs.ValueKind == JsonValueKind.String ? tsEs.GetString() : null
    );
}

public record AdminPlanStopResponseDto(
    Guid Id,
    Guid PlaceId,
    int DayNumber,
    int OrderIndex,
    string? TimeBlock,
    TimeSpan? SuggestedArrival,
    int? SuggestedDurationMin,
    JsonDocument? TravelFromPrevious,
    AdminPlaceDto? Place
)
{
    public static AdminPlanStopResponseDto FromEntity(PlanStop s) => new(
        s.Id, s.PlaceId, s.DayNumber, s.OrderIndex,
        s.TimeBlock, s.SuggestedArrival, s.SuggestedDurationMin,
        s.TravelFromPrevious,
        s.Place is null ? null : AdminPlaceDto.FromEntity(s.Place)
    );
}

public record AdminPlanDayDto(int DayNumber, List<AdminPlanStopResponseDto> Stops);

public record AdminPlanDetailDto(
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
    string Source,
    Guid? CreatedById,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    List<AdminPlanDayDto> Days,
    string? NameEs,
    string? DescriptionEs,
    string? TranslationStatusEs
)
{
    public static AdminPlanDetailDto FromEntity(Plan plan)
    {
        var days = plan.Stops
            .OrderBy(s => s.DayNumber)
            .ThenBy(s => s.OrderIndex)
            .GroupBy(s => s.DayNumber)
            .Select(g => new AdminPlanDayDto(
                g.Key,
                g.Select(AdminPlanStopResponseDto.FromEntity).ToList()
            ))
            .ToList();
        return new AdminPlanDetailDto(
            plan.Id, plan.Name, plan.City, plan.Type, plan.Description, plan.ImageUrl,
            plan.DurationDays, plan.TripContext, plan.IsPublic, plan.IsShowcase,
            plan.Source, plan.CreatedById, plan.CreatedAt, plan.UpdatedAt, days,
            NameEs: LanguageAccessor.ResolveString(plan.NameI18n, "es", null, isCurated: false),
            DescriptionEs: LanguageAccessor.ResolveString(plan.DescriptionI18n, "es", null, isCurated: false),
            TranslationStatusEs: plan.TranslationStatus?.RootElement.TryGetProperty("es", out var tsEs) == true
                && tsEs.ValueKind == JsonValueKind.String ? tsEs.GetString() : null
        );
    }
}

public record AdminPlansListResponse(List<AdminPlanDto> Plans, int Total);

public record AdminPlanCreatedDto(Guid Id, string Name);

public record AdminBulkCreateResultDto(
    int Created,
    int TotalStops,
    List<AdminPlanCreatedDto> Plans
);

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

    [StringLength(20)]
    public string Source { get; set; } = "curated";

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

public class UpdatePlanRequest
{
    [StringLength(255)]
    public string? Name { get; set; }

    [StringLength(100)]
    public string? City { get; set; }

    [StringLength(20)]
    public string? Type { get; set; }

    public string? Description { get; set; }
    public string? ImageUrl { get; set; }

    [Range(1, 7)]
    public int? DurationDays { get; set; }

    public bool? IsPublic { get; set; }
    public bool? IsShowcase { get; set; }

    // i18n ES fields
    public string? NameEs { get; set; }
    public string? DescriptionEs { get; set; }
    // "draft" | "approved" | null (null = no change)
    public string? TranslationStatusEs { get; set; }

    /// <summary>
    /// Optional full stop list. When present, the PATCH replaces ALL stops for the
    /// plan atomically together with the metadata changes (single transaction).
    /// When null, stops are left untouched (metadata-only update — backward compatible).
    /// An empty list clears all stops. This is the atomic replacement for the old
    /// two-call flow (PATCH metadata + PUT /stops) that could leave mixed state if
    /// the second call failed.
    /// </summary>
    public List<CreatePlanStopRequest>? Stops { get; set; }
}

public class UpdatePlanStopsRequest
{
    [Required, MinLength(1)]
    public List<CreatePlanStopRequest> Stops { get; set; } = [];
}
