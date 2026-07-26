using LocalList.API.NET.Features.Builder.Shared;
using LocalList.API.NET.Shared.Dtos;
using LocalList.API.NET.Shared.Routing;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.NET.Features.Builder.Services;

// SchedulingService is split across several partial files by responsibility. This split is
// purely structural (same class, same members, same behavior); it does NOT change any logic:
//   • SchedulingService.cs             — construction + public API entry points
//   • SchedulingService.Constants.cs   — tunables, lookup tables, viability gate constants
//   • SchedulingService.Selection.cs   — pace clamp + weighted day sampling
//   • SchedulingService.Ordering.cs    — geographic ordering + meal/nightlife anchoring
//   • SchedulingService.DayWalk.cs     — day orchestration, segment prefetch, viability clock walk
//   • SchedulingService.Refinements.cs — refinements, time-block matching, stop resolution
//   • SchedulingService.Helpers.cs     — small shared pure helpers (Haversine, durations, blocks)
public partial class SchedulingService
{
    private readonly ILogger<SchedulingService> _logger;
    private readonly ISegmentResolver? _resolver;
    private readonly IConfiguration? _config;

    public SchedulingService(
        ILogger<SchedulingService> logger,
        ISegmentResolver? resolver = null,
        IConfiguration? config = null)
    {
        _logger = logger;
        _resolver = resolver;
        _config = config;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Async version — uses <see cref="ISegmentResolver"/> for real travel durations when available.
    /// Same seed guarantees the same candidate ranking and selection logic.
    /// Travel times may vary (Mapbox vs haversine fallback), which can affect
    /// which stops fit within the day's time budget.
    /// </summary>
    public async Task<ScheduleResult> BuildPlanScheduleAsync(
        List<Place> filteredPlaces, ExtractedPreferences prefs, int? seed = null, CancellationToken ct = default)
    {
        var result = new ScheduleResult();
        var rng = new Random(seed ?? Random.Shared.Next());

        var refined = ApplyRefinements(filteredPlaces, prefs, result);
        var effectiveMaxStops = ResolveEffectiveMaxStops(prefs);
        var dayPlaces = SelectPlacesForDays(refined, prefs, effectiveMaxStops, rng);

        for (int day = 1; day <= prefs.Days; day++)
        {
            if (!dayPlaces.TryGetValue(day, out var places) || places.Count == 0) continue;

            // Map each ordinal day to its real weekday: day 1 = StartDate, day 2 = StartDate+1, …
            // null when the client sent no trip date → day-agnostic legacy gate downstream.
            DayOfWeek? weekday = prefs.StartDate?.AddDays(day - 1).DayOfWeek;
            await ScheduleDayAsync(places, result, day, weekday, rng, ct);
        }

        return result;
    }

    /// <summary>
    /// Sync wrapper for backward compatibility (unit tests). Production code should prefer
    /// <see cref="BuildPlanScheduleAsync"/>.
    /// </summary>
    public ScheduleResult BuildPlanSchedule(
        List<Place> filteredPlaces, ExtractedPreferences prefs, int? seed = null)
        => BuildPlanScheduleAsync(filteredPlaces, prefs, seed).GetAwaiter().GetResult();
}
