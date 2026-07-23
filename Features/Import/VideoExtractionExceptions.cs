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
/// </summary>
public sealed class ExtractionUnavailableException(string reason)
    : VideoExtractionException($"Video extraction is unavailable: {reason}")
{
    public override string Code => "extraction_unavailable";
    public string Reason { get; } = reason;
}
