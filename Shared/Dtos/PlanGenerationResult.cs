using LocalList.API.NET.Shared.Constants;
using LocalList.API.NET.Shared.Data.Entities;
using LocalList.API.NET.Shared.Observability;

namespace LocalList.API.NET.Shared.Dtos;

public class PlanGenerationResult
{
    public required ExtractedPreferences Prefs { get; init; }
    public required ScheduleResult Schedule { get; init; }
    public required List<Place> FilteredPlaces { get; init; }
    public required string PlanName { get; init; }
    public required string PlanDescription { get; init; }
    public required string City { get; init; }
    public required string Lang { get; init; }
    /// <summary>Diagnostics del provider LLM que respondió (multi-provider: gemini/openai/mistral/anthropic).</summary>
    public AiCallDiagnostics? LlmDiagnostics { get; init; }

    /// <summary>
    /// Set cuando los días DERIVADOS por el LLM del texto libre se acotaron al techo del tier
    /// (m3/F6). Null si no hubo clamp. El controller lo traduce al hint estructurado
    /// <c>clamped</c> de la respuesta para que la app pinte el upsell.
    /// </summary>
    public DaysClampInfo? DaysClamp { get; set; }
}

/// <summary>Días pedidos (derivados por el LLM) vs aplicados tras el clamp del techo del tier.</summary>
public sealed record DaysClampInfo(int Requested, int Applied)
{
    /// <summary>
    /// Hint estructurado <c>clamped</c> para la respuesta de generación (m3/F6). Devuelve null
    /// cuando no hubo clamp (la app trata null como "sin recorte"). <c>upsell</c> es true solo
    /// cuando el techo aplicado está por debajo del hard cap global — un free ganaría días
    /// subiendo a Plus; un Plus ya en el tope de 14 días recibe <c>upsell:false</c>. Nombres
    /// de campo ESTABLES (contrato con el task app-side): <c>field</c>, <c>requested</c>,
    /// <c>applied</c>, <c>upsell</c>.
    /// </summary>
    public static object? ToHint(DaysClampInfo? clamp, int appliedMaxDays) =>
        clamp is null ? null : new
        {
            field = "days",
            requested = clamp.Requested,
            applied = clamp.Applied,
            upsell = appliedMaxDays < PlanLimits.MaxPlanDurationDays,
        };
}
