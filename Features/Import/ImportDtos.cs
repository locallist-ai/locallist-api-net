namespace LocalList.API.NET.Features.Import;

/// <summary>
/// Respuesta pública de <c>POST /import/video</c> (F2 T1). Es una proyección DELIBERADAMENTE
/// recortada de <see cref="VideoExtractionResult"/>: solo los candidatos ya saneados + el
/// contexto detectado y la atribución de creador que la app arrastrará al plan (T3/T4).
///
/// NO expone internals de Gemini (file uris, prompts, diagnostics crudos, tokens/coste): esos
/// viven en <c>video_import_metrics</c> para observabilidad admin, no en el cuerpo del cliente.
/// El plan NO se persiste todavía — el matching contra catálogo y la creación del plan son T3/T4.
/// </summary>
public sealed record ImportVideoResponse(
    string? City,
    string? Country,
    string? Language,
    IReadOnlyList<ImportPlaceDto> Places,
    IReadOnlyList<string> Vibes,
    double Confidence,
    string Platform,
    string? CreatorHandle);

/// <summary>
/// Un candidato extraído del vídeo (ya saneado por <see cref="VideoOutputSanitizer"/>), enriquecido
/// por el matcher de T3. Los tres campos de match son ADITIVOS (no rompen el contrato de T1):
/// <see cref="MatchedPlaceId"/> null = el sitio NO está en nuestro catálogo para esa ciudad (la app
/// lo muestra como "no está en LocalList"). <see cref="MatchConfidence"/> = "high" | "medium" | null.
/// </summary>
public sealed record ImportPlaceDto(
    string Name,
    string? Descriptor,
    string? Category,
    string? Evidence,
    int? TimestampSec,
    Guid? MatchedPlaceId = null,
    string? MatchedPlaceName = null,
    string? MatchConfidence = null);

/// <summary>
/// F2 T4 — cuerpo de <c>POST /import/plan</c>. La app envía la ciudad detectada, los días, y los
/// <see cref="PlaceIds"/> que el usuario CONFIRMÓ (subconjunto de los <c>matchedPlaceId</c> de T3).
/// <see cref="Platform"/>/<see cref="CreatorHandle"/> arrastran la atribución del creador desde T1.
/// </summary>
public sealed record CreateImportPlanRequest(
    string City,
    int Days,
    Guid[] PlaceIds,
    string? PlanName = null,
    string? Platform = null,
    string? CreatorHandle = null);

/// <summary>Mapea el resultado del servicio + el matching al DTO público, sin filtrar diagnósticos.</summary>
public static class ImportVideoResponseMapper
{
    public static ImportVideoResponse From(
        VideoExtractionResult result,
        IReadOnlyList<MatchedImportPlace> matched,
        string platform,
        string? creatorHandle) =>
        new(
            result.City,
            result.Country,
            result.Language,
            matched
                .Select(m => new ImportPlaceDto(
                    m.Candidate.Name, m.Candidate.Descriptor, m.Candidate.Category,
                    m.Candidate.Evidence, m.Candidate.TimestampSec,
                    m.MatchedPlaceId, m.MatchedPlaceName, m.MatchConfidence))
                .ToList(),
            result.Vibes,
            result.Confidence,
            platform,
            creatorHandle);
}
