namespace LocalList.API.NET.Shared.Dtos;

public class ExtractedPreferences
{
    public int Days { get; set; } = 1;
    public List<string> Categories { get; set; } = new();
    public List<string> Vibes { get; set; } = new();
    public string GroupType { get; set; } = "couple";
    public string PlanName { get; set; } = "My Plan";
    public string? Description { get; set; }
    public int MaxStopsPerDay { get; set; } = 5;

    // Nuevos campos del wizard (Pablo 2026-04-25/27). Se merge desde
    // TripContextDto en AiProviderService.MergeContextIntoPrefs.
    // PlaceRankingService los usa como soft-signals adicionales.

    /// <summary>Drill-down sub-categorías por categoría top-level (ej. {"food":["sushi","italian"]}).</summary>
    public Dictionary<string, List<string>>? Subcategories { get; set; }
    /// <summary>Tags refinement del groupType (ej. ["honeymoon"] cuando GroupType="couple").</summary>
    public List<string>? CompanyTags { get; set; }
    /// <summary>Tags refinement del style/vibe activo (ej. ["urban","foodie"]).</summary>
    public List<string>? StyleTags { get; set; }
    /// <summary>Presupuesto raw USD/día/persona (fuente para tier match más fino).</summary>
    public int? BudgetAmount { get; set; }

    // ── Refinements — carried from TripContext through to SchedulingService ───
    public string? Pace { get; set; }
    public List<string>? Dietary { get; set; }
    public List<string>? Exclusions { get; set; }
}
