using System.ComponentModel.DataAnnotations;

namespace LocalList.API.NET.Features.Builder;

public class BuilderChatRequest
{
    // Message opcional (Pablo 2026-04-23): el chat complementa al wizard pero no lo sustituye.
    // Si el user no escribe nada, el wizard debe tener mínimo 3/5 señales para generar plan.
    // Si el user escribe algo, se pasa tal cual al pipeline (Gemini + embedding query).
    [MaxLength(5000)]
    public string? Message { get; set; }
    public TripContextDto? TripContext { get; set; }
}

public class ExtractedPreferences
{
    public int Days { get; set; } = 1;
    public List<string> Categories { get; set; } = new();
    public List<string> Vibes { get; set; } = new();
    public string GroupType { get; set; } = "couple";
    public string PlanName { get; set; } = "My Plan";
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
}

public class TripContextDto
{
    [MaxLength(20)]
    public string? GroupType { get; set; }
    [Range(1, 7)]
    public int? Days { get; set; }
    [MaxLength(100)]
    public string? City { get; set; }
    [MaxLength(20)]
    public string? Budget { get; set; } // "budget" | "moderate" | "premium"

    /// <summary>
    /// Presupuesto diario por persona en USD (custom input). Pablo 2026-04-25:
    /// "en vez de 3 tabs, deberíamos dejar que el usuario ponga su presupuesto".
    /// El frontend sigue mandando `Budget` con el tier derivado (budget/moderate/
    /// premium) para compat con el matching actual. `BudgetAmount` viaja como
    /// contexto extra para futuro matching más fino.
    /// </summary>
    [Range(0, 10000)]
    public int? BudgetAmount { get; set; }

    /// <summary>
    /// Top-level interests seleccionados en el nuevo step del wizard
    /// (ej. "food", "outdoors", "culture"). Mapean contra Place.Category.
    /// Pablo 2026-04-25: campo additive, el matching contra el catálogo se
    /// implementa en sesión siguiente. De momento se acepta y se loggea.
    /// </summary>
    [MaxLength(10)]
    public List<string>? Categories { get; set; }

    /// <summary>
    /// Drill-down por categoría: { "food": ["sushi","italian"], "outdoors": ["beach"] }.
    /// Mapean contra Place.Subcategory mediante substring match (ver helper en
    /// memoria project_real_routing_pending para el patrón). Cada lista tiene
    /// como máximo 10 tags y los strings van validados aparte si se persisten.
    /// </summary>
    public Dictionary<string, List<string>>? Subcategories { get; set; }

    /// <summary>
    /// Drill-down tags del company step seleccionado (ej. ["honeymoon"] cuando
    /// GroupType="couple"). Solo se envían los del parent activo. Pablo
    /// 2026-04-25: drill-down también para company y style. Mapean contra
    /// Place.SuitableFor / BestFor en el catálogo.
    /// </summary>
    [MaxLength(5)]
    public List<string>? CompanyTags { get; set; }

}
