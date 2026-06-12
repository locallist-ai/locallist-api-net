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
}
