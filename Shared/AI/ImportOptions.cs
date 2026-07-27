namespace LocalList.API.NET.Shared.AI;

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

    /// <summary>Rechazo pre-subida: VÍDEO mayor que esto → VideoTooLarge.</summary>
    public long MaxSizeBytes { get; set; } = 150L * 1024 * 1024; // 150 MB

    /// <summary>
    /// Rechazo pre-subida: IMAGEN mayor que esto → VideoTooLarge. Cap separado y mucho más bajo
    /// que el de vídeo: una captura / foto de itinerario / carrusel es pequeña (típ. &lt; 5 MB),
    /// y 25 MB deja margen holgado para HEIC/PNG a resolución alta sin abrir la puerta a subir un
    /// blob de 150 MB por el camino de imagen. La File API de Gemini procesa imágenes nativamente
    /// y SIN coste por duración (no hay 258 tok/s de vídeo), así que el import de imagen es más
    /// barato y no necesita el cap ancho del vídeo.
    /// </summary>
    public long MaxImageSizeBytes { get; set; } = 25L * 1024 * 1024; // 25 MB

    /// <summary>MIME allowlist de VÍDEO (rechazo pre-subida). mp4 / mov (quicktime) / webm.</summary>
    public string[] AllowedMimeTypes { get; set; } =
        { "video/mp4", "video/quicktime", "video/webm" };

    /// <summary>
    /// MIME allowlist de IMAGEN (rechazo pre-subida). jpeg / png / webp / heic (heic = fotos de
    /// iPhone). Lista SEPARADA de <see cref="AllowedMimeTypes"/> a propósito: aguas abajo hay que
    /// distinguir vídeo de imagen para SALTAR el check de duración (una imagen no tiene duración;
    /// el File API devuelve null y eso es LEGÍTIMO, no un fallo). Con una allowlist mezclada no se
    /// podría separar ese camino ni aplicar caps de tamaño distintos.
    /// </summary>
    public string[] AllowedImageMimeTypes { get; set; } =
        { "image/jpeg", "image/png", "image/webp", "image/heic" };

    /// <summary>Espera entre polls de <c>files.get</c> mientras el fichero está PROCESSING.</summary>
    public int FilePollDelayMs { get; set; } = 1000;

    /// <summary>Máximo de polls antes de rendirse (ExtractionUnavailable). 60 × 1s = 60s.</summary>
    public int FilePollMaxAttempts { get; set; } = 60;

    /// <summary>
    /// Gating del camino de TERCEROS (URL de TikTok/IG en vez de contenido propio del usuario).
    /// Default <b>false</b>: en v1 el import solo acepta contenido PROPIO (<c>platform=self</c>);
    /// una request con <c>platform</c> distinto de <c>self</c> se rechaza con
    /// <c>403 third_party_import_disabled</c> mientras esté apagado. La capability se expone en
    /// <c>GET /account</c> (<c>importThirdPartyEnabled</c>) para que la app oculte la opción.
    /// </summary>
    public bool ThirdPartyEnabled { get; set; } = false;

    /// <summary>¿El MIME (ya normalizado a minúsculas) es un vídeo permitido?</summary>
    public bool IsVideoMime(string mimeType) => AllowedMimeTypes.Contains(mimeType);

    /// <summary>¿El MIME (ya normalizado a minúsculas) es una imagen permitida?</summary>
    public bool IsImageMime(string mimeType) => AllowedImageMimeTypes.Contains(mimeType);

    /// <summary>¿El MIME es un tipo de media permitido (vídeo O imagen)?</summary>
    public bool IsAllowedMime(string mimeType) => IsVideoMime(mimeType) || IsImageMime(mimeType);

    /// <summary>
    /// Cap de tamaño aplicable a un MIME: <see cref="MaxImageSizeBytes"/> para imágenes,
    /// <see cref="MaxSizeBytes"/> para vídeo (y como fallback conservador para MIME desconocidos,
    /// que de todas formas se rechazan antes por la allowlist).
    /// </summary>
    public long SizeCapFor(string mimeType) => IsImageMime(mimeType) ? MaxImageSizeBytes : MaxSizeBytes;
}
