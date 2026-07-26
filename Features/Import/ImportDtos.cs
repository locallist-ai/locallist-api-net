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

/// <summary>Un candidato extraído del vídeo (ya saneado por <see cref="VideoOutputSanitizer"/>).</summary>
public sealed record ImportPlaceDto(
    string Name,
    string? Descriptor,
    string? Category,
    string? Evidence,
    int? TimestampSec);

/// <summary>Mapea el resultado del servicio al DTO público, sin filtrar diagnósticos.</summary>
public static class ImportVideoResponseMapper
{
    public static ImportVideoResponse From(
        VideoExtractionResult result, string platform, string? creatorHandle) =>
        new(
            result.City,
            result.Country,
            result.Language,
            result.Places
                .Select(p => new ImportPlaceDto(p.Name, p.Descriptor, p.Category, p.Evidence, p.TimestampSec))
                .ToList(),
            result.Vibes,
            result.Confidence,
            platform,
            creatorHandle);
}
