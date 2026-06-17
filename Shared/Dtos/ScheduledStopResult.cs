namespace LocalList.API.NET.Shared.Dtos;

// Forma tipada del resultado de ResolveStopPlaces (antes un anonymous type
// boxed como object en la interfaz pública IPlanGenerationService).
// Serializa en camelCase via la política JSON por defecto — el contrato de
// respuesta de /builder/chat y /chat/generate es idéntico al previo.
public sealed record ResolvedPlaceDto(
    Guid Id,
    string Name,
    string Category,
    string? Neighborhood,
    string WhyThisPlace,
    string? PriceRange,
    List<string>? Photos,
    decimal? Latitude,
    decimal? Longitude);

public sealed record ScheduledStopResult(
    Guid Id,
    Guid PlaceId,
    int DayNumber,
    int OrderIndex,
    string TimeBlock,
    string? SuggestedArrival,
    int SuggestedDurationMin,
    TravelInfoDto? TravelFromPrevious,
    ResolvedPlaceDto? Place);
