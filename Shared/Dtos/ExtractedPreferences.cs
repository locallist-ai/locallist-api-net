using System.Text.Json.Serialization;

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

    /// <summary>
    /// Tier de presupuesto del wizard: "budget" | "moderate" | "premium".
    /// JsonIgnore: solo lo asigna MergeContextIntoPrefs desde TripContextDto —
    /// el JSON del LLM no puede inyectarlo.
    /// </summary>
    [JsonIgnore]
    public string? BudgetTier { get; set; }

    /// <summary>
    /// True cuando Categories viene de una elección explícita del usuario (wizard/chat
    /// slots) y no de la extracción del LLM. Activa el gate duro de categoría en el
    /// retrieval RAG. JsonIgnore: solo lo asigna MergeContextIntoPrefs.
    /// </summary>
    [JsonIgnore]
    public bool CategoriesExplicit { get; set; }

    // ── Refinements — carried from TripContext through to SchedulingService ───

    /// <summary>
    /// Trip pace: "slow" | "normal" | "fast". JsonIgnore: solo lo asigna
    /// MergeContextIntoPrefs desde TripContextDto — el JSON del LLM no puede
    /// inyectarlo. Si el LLM pudiera setearlo, ResolveEffectiveMaxStops
    /// (scheduler) y el needed del gate de categoría divergirían del clamp
    /// que MergeContextIntoPrefs aplica sobre MaxStopsPerDay.
    /// </summary>
    [JsonIgnore]
    public string? Pace { get; set; }
    public List<string>? Dietary { get; set; }
    public List<string>? Exclusions { get; set; }
}
