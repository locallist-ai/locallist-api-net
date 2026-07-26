namespace LocalList.API.NET.Features.Import;

/// <summary>
/// Configuración de la slice de import de vídeo (F2). Bind desde config "Import".
///
/// El import NO participa en la cadena de fallback <c>Llm:Providers</c>: solo Gemini
/// tiene el fichero subido vía File API, así que si Gemini falla la extracción falla
/// (retry manual del usuario) — no hay sentido en reintentar con OpenAI/Mistral, que
/// no ven el vídeo.
///
/// La API key se resuelve como <c>Import:ApiKey</c> con fallback a <c>Gemini:ApiKey</c>
/// (misma cuenta Gemini; la clave separada solo existe para poder aislar cuota/coste del
/// import si algún día conviene).
/// </summary>
public sealed class ImportOptions
{
    public const string SectionName = "Import";

    /// <summary>
    /// Modelo multimodal. <b>gemini-3.1-flash</b> (NO lite): el import es OCR-pesado
    /// (texto sobreimpreso, carteles, subtítulos quemados) y flash-lite pierde recall
    /// sobre texto pequeño. El coste extra se absorbe: un import es puntual, no un loop.
    /// </summary>
    public string Model { get; set; } = "gemini-3.1-flash";

    /// <summary>Rechazo pre-subida: vídeo más largo que esto (verificado contra File API) → VideoTooLong.</summary>
    public int MaxDurationSeconds { get; set; } = 600; // 10 min

    /// <summary>Rechazo pre-subida: fichero mayor que esto → VideoTooLarge.</summary>
    public long MaxSizeBytes { get; set; } = 150L * 1024 * 1024; // 150 MB

    /// <summary>MIME allowlist (rechazo pre-subida). mp4 / mov (quicktime) / webm.</summary>
    public string[] AllowedMimeTypes { get; set; } =
        { "video/mp4", "video/quicktime", "video/webm" };

    /// <summary>Espera entre polls de <c>files.get</c> mientras el fichero está PROCESSING.</summary>
    public int FilePollDelayMs { get; set; } = 1000;

    /// <summary>Máximo de polls antes de rendirse (ExtractionUnavailable). 60 × 1s = 60s.</summary>
    public int FilePollMaxAttempts { get; set; } = 60;
}
