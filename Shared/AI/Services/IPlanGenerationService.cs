using LocalList.API.NET.Shared.Data.Entities;
using LocalList.API.NET.Shared.Dtos;

namespace LocalList.API.NET.Shared.AI.Services;

public interface IPlanGenerationService
{
    /// <param name="maxDays">Techo de días del plan según tier (free 3 / Plus 14). Acota
    /// también los días que el LLM derive del texto libre — el gate del controller solo ve
    /// los días pedidos explícitamente, así que este clamp es lo que hace el límite de
    /// duración inviolable por construcción.</param>
    Task<PlanGenerationResult?> GenerateAsync(string? message, TripContextDto? tripContext, string lang, int maxDays, CancellationToken ct);
    IEnumerable<ScheduledStopResult> ResolveStopPlaces(List<ScheduledStopDto> stops, List<Place> allPlaces);
}
