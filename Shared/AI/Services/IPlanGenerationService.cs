using LocalList.API.NET.Shared.Data.Entities;
using LocalList.API.NET.Shared.Dtos;

namespace LocalList.API.NET.Shared.AI.Services;

public interface IPlanGenerationService
{
    Task<PlanGenerationResult?> GenerateAsync(string? message, TripContextDto? tripContext, string lang, CancellationToken ct);
    IEnumerable<ScheduledStopResult> ResolveStopPlaces(List<ScheduledStopDto> stops, List<Place> allPlaces);
}
