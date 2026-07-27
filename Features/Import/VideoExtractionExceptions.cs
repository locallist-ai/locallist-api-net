using LocalList.API.NET.Shared.AI;

namespace LocalList.API.NET.Features.Import;

/// <summary>
/// Base de los fallos tipados de la extracción de vídeo. El endpoint (T1) mapea cada
/// subtipo a un status/copy concreto; el servicio nunca devuelve nulls silenciosos.
/// </summary>
public abstract class VideoExtractionException(string message) : Exception(message)
{
    /// <summary>Código estable para diagnóstico admin / mapping de status en el endpoint.</summary>
    public abstract string Code { get; }
}

/// <summary>Vídeo más largo que <see cref="ImportOptions.MaxDurationSeconds"/> (verificado contra metadata del File API).</summary>
public sealed class VideoTooLongException(double durationSec, int maxSec)
    : VideoExtractionException($"Video is {durationSec:F0}s, exceeds the {maxSec}s limit.")
{
    public override string Code => "video_too_long";
    public double DurationSec { get; } = durationSec;
    public int MaxSec { get; } = maxSec;
}

/// <summary>Fichero mayor que <see cref="ImportOptions.MaxSizeBytes"/> (rechazo pre-subida).</summary>
public sealed class VideoTooLargeException(long sizeBytes, long maxBytes)
    : VideoExtractionException($"Video is {sizeBytes} bytes, exceeds the {maxBytes} byte limit.")
{
    public override string Code => "video_too_large";
    public long SizeBytes { get; } = sizeBytes;
    public long MaxBytes { get; } = maxBytes;
}

/// <summary>MIME fuera de la allowlist (rechazo pre-subida).</summary>
public sealed class VideoUnsupportedFormatException(string mimeType)
    : VideoExtractionException($"Unsupported video format '{mimeType}'.")
{
    public override string Code => "video_unsupported_format";
    public string MimeType { get; } = mimeType;
}

/// <summary>El modelo procesó el vídeo pero no identificó ningún sitio (places vacío tras sanitizar).</summary>
public sealed class NoPlacesFoundException()
    : VideoExtractionException("No identifiable places were extracted from the video.")
{
    public override string Code => "no_places_found";
}

/// <summary>
/// Fallo de infraestructura: key ausente, File API caída, generateContent no-2xx,
/// respuesta truncada/filtrada, o JSON irrecuperable. El usuario reintenta manualmente.
///
/// <see cref="Billed"/> distingue si Gemini YA FACTURÓ la llamada multimodal cuando ocurrió el
/// fallo: <c>truncated</c> (MAX_TOKENS), <c>content_filtered_*</c> (SAFETY y afines) e
/// <c>invalid_json</c> llegan DESPUÉS de un 2xx de generateContent — los ~150k tokens de input
/// del vídeo se cobraron aunque el resultado sea inservible. El endpoint (T1) usa este flag
/// para decidir si la cuota del usuario se mantiene consumida (billed) o se reembolsa (fallo
/// pre-facturación: upload, poll, duration_unknown, HTTP no-2xx de generate). Sin el flag, un
/// atacante dispararía llamadas caras a voluntad (el CONTENIDO del vídeo controla MAX_TOKENS/
/// SAFETY/JSON roto) con cuota siempre reembolsada.
/// </summary>
public sealed class ExtractionUnavailableException(string reason, bool billed = false)
    : VideoExtractionException($"Video extraction is unavailable: {reason}")
{
    public override string Code => "extraction_unavailable";
    public string Reason { get; } = reason;

    /// <summary>true si generateContent devolvió 2xx antes del fallo (Gemini cobró los tokens).</summary>
    public bool Billed { get; } = billed;
}
