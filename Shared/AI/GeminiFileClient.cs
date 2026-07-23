using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LocalList.API.NET.Features.Import;
using Microsoft.Extensions.Options;

namespace LocalList.API.NET.Shared.AI;

/// <summary>Metadata de un fichero en la Gemini File API. Duration solo llega en vídeos ACTIVE.</summary>
public sealed record GeminiFile(
    string Name,       // "files/abc123"
    string Uri,        // https URI referenciable desde generateContent (file_uri)
    string MimeType,
    long? SizeBytes,
    string State,      // PROCESSING | ACTIVE | FAILED
    double? DurationSec);

public interface IGeminiFileClient
{
    /// <summary>Subida resumable (start → upload+finalize). Devuelve el fichero, normalmente en PROCESSING.</summary>
    Task<GeminiFile> UploadAsync(Stream content, string mimeType, long sizeBytes, string displayName, CancellationToken ct = default);

    /// <summary>Poll de files.get hasta ACTIVE. Lanza si el fichero pasa a FAILED o se agotan los intentos.</summary>
    Task<GeminiFile> WaitUntilActiveAsync(string fileName, CancellationToken ct = default);

    /// <summary>Borrado explícito (files.delete). Minimiza la retención de contenido de terceros.</summary>
    Task DeleteAsync(string fileName, CancellationToken ct = default);
}

/// <summary>
/// Cliente de la Gemini File API (<c>generativelanguage.googleapis.com</c>) para el import de
/// vídeo. Vive junto a los clientes LLM de texto de <c>Shared/AI/Llm</c> pero es aparte: el
/// import sube bytes de vídeo, no texto, y no participa en la cadena de fallback.
///
/// Protocolo de subida resumable en dos tramos:
///   1. POST /upload/v1beta/files con <c>X-Goog-Upload-Command: start</c> → devuelve la URL de
///      sesión en la cabecera <c>X-Goog-Upload-URL</c>.
///   2. POST a esa URL con <c>X-Goog-Upload-Command: upload, finalize</c> y los bytes → devuelve
///      el recurso <c>file</c> (normalmente en PROCESSING).
///
/// El vídeo se transcodifica async en el lado de Google; hay que hacer poll de <c>files.get</c>
/// hasta <c>ACTIVE</c> antes de referenciarlo en generateContent. La duración autoritativa
/// (<c>videoMetadata.videoDuration</c>) solo aparece cuando el fichero está ACTIVE — de ahí que
/// el límite de duración se verifique aquí, no en el cliente por el tamaño del stream.
/// </summary>
public sealed class GeminiFileClient : IGeminiFileClient
{
    private const string BaseUrl = "https://generativelanguage.googleapis.com";

    private readonly HttpClient _http;
    private readonly IConfiguration _config;
    private readonly ImportOptions _options;
    private readonly ILogger<GeminiFileClient> _logger;

    public GeminiFileClient(
        HttpClient http,
        IConfiguration config,
        IOptions<ImportOptions> options,
        ILogger<GeminiFileClient> logger)
    {
        _http = http;
        _config = config;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Import:ApiKey con fallback a Gemini:ApiKey (misma cuenta). Vacío → ExtractionUnavailable.</summary>
    private string ResolveApiKey()
    {
        var key = _config["Import:ApiKey"];
        if (string.IsNullOrEmpty(key)) key = _config["Gemini:ApiKey"];
        if (string.IsNullOrEmpty(key))
            throw new ExtractionUnavailableException("missing_key");
        return key;
    }

    public async Task<GeminiFile> UploadAsync(
        Stream content, string mimeType, long sizeBytes, string displayName, CancellationToken ct = default)
    {
        var apiKey = ResolveApiKey();

        // 1. Iniciar la sesión resumable. El cuerpo lleva solo la metadata (display_name);
        // los bytes van en el segundo tramo.
        using var startReq = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/upload/v1beta/files");
        startReq.Headers.Add("x-goog-api-key", apiKey);
        startReq.Headers.Add("X-Goog-Upload-Protocol", "resumable");
        startReq.Headers.Add("X-Goog-Upload-Command", "start");
        startReq.Headers.Add("X-Goog-Upload-Header-Content-Length", sizeBytes.ToString(CultureInfo.InvariantCulture));
        startReq.Headers.Add("X-Goog-Upload-Header-Content-Type", mimeType);
        startReq.Content = new StringContent(
            JsonSerializer.Serialize(new { file = new { display_name = displayName } }),
            Encoding.UTF8, "application/json");

        using var startResp = await _http.SendAsync(startReq, ct);
        if (!startResp.IsSuccessStatusCode)
        {
            _logger.LogError("File API: resumable start returned {Status}", (int)startResp.StatusCode);
            throw new ExtractionUnavailableException($"upload_start_http_{(int)startResp.StatusCode}");
        }

        var uploadUrl = startResp.Headers.TryGetValues("X-Goog-Upload-URL", out var vals)
            ? vals.FirstOrDefault()
            : null;
        if (string.IsNullOrEmpty(uploadUrl))
        {
            _logger.LogError("File API: resumable start missing X-Goog-Upload-URL header");
            throw new ExtractionUnavailableException("upload_url_missing");
        }

        // 2. Subir los bytes y finalizar en una sola petición.
        using var uploadReq = new HttpRequestMessage(HttpMethod.Post, uploadUrl);
        uploadReq.Headers.Add("x-goog-api-key", apiKey);
        uploadReq.Headers.Add("X-Goog-Upload-Offset", "0");
        uploadReq.Headers.Add("X-Goog-Upload-Command", "upload, finalize");
        var streamContent = new StreamContent(content);
        streamContent.Headers.ContentLength = sizeBytes;
        uploadReq.Content = streamContent;

        using var uploadResp = await _http.SendAsync(uploadReq, ct);
        if (!uploadResp.IsSuccessStatusCode)
        {
            _logger.LogError("File API: upload/finalize returned {Status}", (int)uploadResp.StatusCode);
            throw new ExtractionUnavailableException($"upload_finalize_http_{(int)uploadResp.StatusCode}");
        }

        var body = await uploadResp.Content.ReadAsStringAsync(ct);

        // El finalize devolvió 2xx: el fichero YA existe server-side en Gemini. Si el parseo del
        // cuerpo falla (truncado/malformado), NO podemos dejarlo huérfano — sería retención de
        // contenido de terceros. Extraemos el name de forma tolerante y lo borramos best-effort
        // antes de propagar el fallo, de modo que un fichero creado SIEMPRE se pueda borrar.
        GeminiFile file;
        try
        {
            file = ParseFile(body);
        }
        catch (Exception ex)
        {
            var orphan = TryExtractFileName(body);
            if (!string.IsNullOrEmpty(orphan))
            {
                _logger.LogError(ex,
                    "File API: 2xx finalize but unparseable body; best-effort deleting orphan {File}", orphan);
                await TryBestEffortDeleteAsync(orphan, apiKey);
            }
            else
            {
                _logger.LogError(ex,
                    "File API: 2xx finalize but unparseable body and no recoverable file name (possible orphan)");
            }
            throw new ExtractionUnavailableException("upload_parse_failed");
        }

        // Defensa: un 2xx sin name deja un fichero que el servicio no podría borrar en su finally.
        // Simetría con el catch de arriba: aunque el JSON fuese válido, si carga un name vacío
        // intentamos rescatar un "files/…" del cuerpo (por si el recurso viaja anidado en otra
        // clave que ParseFile no leyó) y lo borramos best-effort antes de propagar el fallo.
        // En la práctica es inalcanzable rescatar nada: la File API SIEMPRE devuelve
        // {"name":"files/…"} en un 2xx, así que un name vacío implica cuerpo sin ese campo y el
        // regex tampoco lo encontrará. La rama existe por simetría/defensa, no porque sea normal.
        if (string.IsNullOrEmpty(file.Name))
        {
            var orphan = TryExtractFileName(body);
            if (!string.IsNullOrEmpty(orphan))
            {
                _logger.LogError(
                    "File API: 2xx finalize parsed no file name; best-effort deleting recovered orphan {File}", orphan);
                await TryBestEffortDeleteAsync(orphan, apiKey);
            }
            else
            {
                _logger.LogError("File API: 2xx finalize but response carried no file name");
            }
            throw new ExtractionUnavailableException("upload_no_name");
        }

        return file;
    }

    // Nombre de fichero de la File API: "files/xyz". Tolerante a cuerpos NO-JSON (truncados).
    private static readonly Regex FileNameRegex =
        new(@"""name""\s*:\s*""(files/[^""\\]+)""", RegexOptions.Compiled);

    /// <summary>Extrae "files/xyz" de un cuerpo aunque no sea JSON completo/parseable.</summary>
    private static string? TryExtractFileName(string? body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        var m = FileNameRegex.Match(body);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Borrado que nunca lanza: limpia huérfanos sin enmascarar el fallo real de subida.</summary>
    private async Task TryBestEffortDeleteAsync(string fileName, string apiKey)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Delete, $"{BaseUrl}/v1beta/{fileName}");
            req.Headers.Add("x-goog-api-key", apiKey);
            using var resp = await _http.SendAsync(req, CancellationToken.None);
            if (!resp.IsSuccessStatusCode)
                _logger.LogError("File API: best-effort orphan delete returned {Status} for {File}",
                    (int)resp.StatusCode, fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "File API: best-effort orphan delete threw for {File}", fileName);
        }
    }

    public async Task<GeminiFile> WaitUntilActiveAsync(string fileName, CancellationToken ct = default)
    {
        var apiKey = ResolveApiKey();
        var url = $"{BaseUrl}/v1beta/{fileName}";

        for (var attempt = 1; attempt <= _options.FilePollMaxAttempts; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("x-goog-api-key", apiKey);

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogError("File API: files.get returned {Status} for {File}", (int)resp.StatusCode, fileName);
                throw new ExtractionUnavailableException($"file_get_http_{(int)resp.StatusCode}");
            }

            var body = await resp.Content.ReadAsStringAsync(ct);
            var file = ParseFile(body);

            switch (file.State)
            {
                case "ACTIVE":
                    return file;
                case "FAILED":
                    _logger.LogError("File API: processing FAILED for {File}", fileName);
                    throw new ExtractionUnavailableException("file_processing_failed");
                default:
                    // PROCESSING (u otro estado transitorio): esperar y reintentar.
                    if (attempt < _options.FilePollMaxAttempts)
                        await Task.Delay(_options.FilePollDelayMs, ct);
                    break;
            }
        }

        _logger.LogError("File API: {File} not ACTIVE after {Attempts} polls", fileName, _options.FilePollMaxAttempts);
        throw new ExtractionUnavailableException("file_processing_timeout");
    }

    public async Task DeleteAsync(string fileName, CancellationToken ct = default)
    {
        var apiKey = ResolveApiKey();
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"{BaseUrl}/v1beta/{fileName}");
        req.Headers.Add("x-goog-api-key", apiKey);

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            // No lanzamos VideoExtractionException aquí para no enmascarar el resultado real
            // de la extracción; el caller llama a Delete en un finally best-effort. Pero sí
            // dejamos rastro: un fichero no borrado es un problema de retención.
            _logger.LogError("File API: delete returned {Status} for {File}", (int)resp.StatusCode, fileName);
            throw new ExtractionUnavailableException($"file_delete_http_{(int)resp.StatusCode}");
        }
    }

    /// <summary>
    /// Parsea tanto la respuesta de upload/finalize (<c>{"file":{...}}</c>) como la de
    /// files.get (el recurso file en la raíz). Tolera campos ausentes.
    /// </summary>
    private static GeminiFile ParseFile(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement.TryGetProperty("file", out var wrapped) ? wrapped : doc.RootElement;

        var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        var uri = root.TryGetProperty("uri", out var u) ? u.GetString() ?? "" : "";
        var mime = root.TryGetProperty("mimeType", out var m) ? m.GetString() ?? "" : "";
        var state = root.TryGetProperty("state", out var s) ? s.GetString() ?? "" : "";

        long? sizeBytes = null;
        if (root.TryGetProperty("sizeBytes", out var sz))
        {
            // La File API serializa int64 como string ("123456").
            if (sz.ValueKind == JsonValueKind.String && long.TryParse(sz.GetString(), out var parsed))
                sizeBytes = parsed;
            else if (sz.ValueKind == JsonValueKind.Number && sz.TryGetInt64(out var num))
                sizeBytes = num;
        }

        double? durationSec = null;
        if (root.TryGetProperty("videoMetadata", out var vm) &&
            vm.TryGetProperty("videoDuration", out var vd) &&
            vd.ValueKind == JsonValueKind.String)
        {
            durationSec = ParseProtoDuration(vd.GetString());
        }

        return new GeminiFile(name, uri, mime, sizeBytes, state, durationSec);
    }

    /// <summary>Duración proto3 (JSON) → segundos. Formato "12.5s" / "700s".</summary>
    private static double? ParseProtoDuration(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        var trimmed = value.EndsWith('s') ? value[..^1] : value;
        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? seconds
            : null;
    }
}
