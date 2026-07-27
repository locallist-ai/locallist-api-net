using LocalList.API.NET.Shared.Data.Entities;
using LocalList.API.NET.Shared.Dtos;

namespace LocalList.API.NET.Shared.AI.Services;

/// <summary>
/// Contrato cross-slice del scheduler determinista por semilla. Vive en Shared porque lo
/// consumen varias features: Builder lo usa para generar planes IA; Import (F2 T4) lo reusa
/// para materializar el plan a partir de un set FIJO de places confirmados. La implementación
/// (<c>SchedulingService</c>, partial) vive en <c>Features/Builder/Services/</c> — Import no
/// depende del tipo concreto, solo de esta firma (mismo patrón que <c>IPlanGenerationService</c>).
/// </summary>
public interface ISchedulingService
{
    /// <summary>
    /// Reparte <paramref name="filteredPlaces"/> en <c>prefs.Days</c> días de forma DETERMINISTA
    /// para una misma <paramref name="seed"/> (misma semilla → mismo plan). Usa
    /// <c>ISegmentResolver</c> para tiempos de viaje reales cuando está disponible.
    /// </summary>
    Task<ScheduleResult> BuildPlanScheduleAsync(
        List<Place> filteredPlaces, ExtractedPreferences prefs, int? seed = null, CancellationToken ct = default);
}
