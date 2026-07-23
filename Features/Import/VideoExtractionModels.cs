using LocalList.API.NET.Shared.Observability;

namespace LocalList.API.NET.Features.Import;

/// <summary>
/// Un sitio extraído de un vídeo. Todos los campos de texto ya vienen sanitizados
/// (sin URLs, sin markdown/HTML, longitudes acotadas, categoría contra taxonomía).
/// </summary>
public sealed record ExtractedVideoPlace(
    string Name,
    string? Descriptor,
    string? Category,
    string? Evidence,
    int? TimestampSec);

/// <summary>
/// Resultado de la extracción: contexto de ciudad/idioma + sitios + vibes + confianza.
/// <see cref="Diagnostics"/> lleva tokens/coste/latencia (persistido en video_import_metrics).
/// </summary>
public sealed record VideoExtractionResult(
    string? City,
    string? Country,
    string? Language,
    IReadOnlyList<ExtractedVideoPlace> Places,
    IReadOnlyList<string> Vibes,
    double Confidence,
    AiCallDiagnostics Diagnostics);
