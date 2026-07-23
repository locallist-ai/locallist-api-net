using System.ComponentModel.DataAnnotations;

namespace LocalList.API.NET.Shared.Dtos;

public class TripContextDto
{
    /// <summary>Max trip horizon: a start date further out than this is rejected (plan R2 default).</summary>
    public const int MaxTripHorizonDays = 365;

    [MaxLength(20)]
    public string? GroupType { get; set; }

    // [Range(1,14)]: el wizard Plus ofrece hasta 14 días (maxDaysForTier). Antes
    // estaba en [1,7] → un Plus con 8-14 días recibía un 400 de model-binding.
    // NOTA: el clamp a [1,7] en PreferenceExtractorService/SlotExtractorService
    // sigue vigente (fuera del scope de esta task de contrato) — soportar >7 días
    // end-to-end es trabajo del slice del scheduler.
    [Range(1, 14)]
    public int? Days { get; set; }

    /// <summary>
    /// Fecha de inicio del viaje (fecha de calendario, sin zona horaria — el scheduler
    /// opera en reloj local abstracto). Serializa como "yyyy-MM-dd". Nullable = compat con
    /// clientes viejos que no la envían (fallback day-agnostic en el gate de horarios).
    /// Cimiento de la viabilidad temporal (día-de-semana correcto) y de la monetización
    /// futura por reservas/comisiones. Validada en el controller: 400 invalid_start_date.
    /// </summary>
    public DateOnly? StartDate { get; set; }

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

    // ── Refinements (chat slots PR 3) ─────────────────────────────────────────

    /// <summary>Trip pace: "slow" | "normal" | "fast". Affects MaxStopsPerDay clamp.</summary>
    [MaxLength(10)]
    public string? Pace { get; set; }

    /// <summary>Dietary restrictions: vegetarian | vegan | halal | gluten_free | none.</summary>
    [MaxLength(5)]
    public List<string>? Dietary { get; set; }

    /// <summary>Categories/vibes the user wants to avoid (e.g. "nightlife", "touristy").</summary>
    [MaxLength(5)]
    public List<string>? Exclusions { get; set; }

    /// <summary>Primary vibe from chat slot-filling (e.g. "hidden_gems", "romantic").</summary>
    [MaxLength(30)]
    public string? VibesPrimary { get; set; }

    /// <summary>
    /// True cuando <paramref name="startDate"/> es null (compat) o cae dentro de la ventana
    /// permitida respecto a <paramref name="today"/>: no antes de ayer (margen de 1 día que
    /// absorbe el desfase de zona horaria cliente/servidor) y a lo sumo
    /// <see cref="MaxTripHorizonDays"/> días en el futuro.
    /// </summary>
    public static bool IsStartDateWithinWindow(DateOnly? startDate, DateOnly today)
    {
        if (startDate is null) return true; // nullable = compat clientes viejos
        var d = startDate.Value;
        return d >= today.AddDays(-1) && d <= today.AddDays(MaxTripHorizonDays);
    }
}
