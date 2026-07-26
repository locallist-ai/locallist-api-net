using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using LocalList.API.NET.Shared.Auth;
using LocalList.API.NET.Shared.Data;
using LocalList.API.NET.Shared.Usage;

namespace LocalList.API.NET.Features.Import;

/// <summary>
/// Quita los value-provider factories de formulario del pipeline de MVC para la acción marcada.
/// SIN esto, con un cuerpo <c>multipart/form-data</c> MVC llama a <c>Request.ReadFormAsync()</c>
/// al construir los value providers (aunque la acción no tenga parámetros <c>[FromForm]</c>),
/// consumiendo el body ANTES de que el <see cref="MultipartReader"/> lo lea → IOException
/// "the content may have already been read". Patrón canónico de streaming de subidas grandes.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class DisableFormValueModelBindingAttribute : Attribute, IResourceFilter
{
    public void OnResourceExecuting(ResourceExecutingContext context)
    {
        var factories = context.ValueProviderFactories;
        factories.RemoveType<FormValueProviderFactory>();
        factories.RemoveType<FormFileValueProviderFactory>();
        factories.RemoveType<JQueryFormValueProviderFactory>();
    }

    public void OnResourceExecuted(ResourceExecutedContext context) { }
}

/// <summary>
/// F2 T1 — endpoint público de import de vídeo. Consumidor de <see cref="VideoExtractionService"/>
/// (F2 T2): recibe el vídeo por multipart, lo streamea a un temp file y se lo pasa al servicio,
/// que sube a la Gemini File API, extrae sitios y borra el fichero. El endpoint NO persiste plan
/// todavía (matching + creación = T3/T4); devuelve los candidatos saneados como DTO propio.
///
/// Capas de defensa, en orden (cada una rechaza barato antes que la siguiente pague coste):
///   1. <c>[Authorize]</c> AppScheme  → 401 anónimo.
///   2. Gate Plus (tier FRESCO de DB) → 403 <c>import_requires_plus</c> (import = feature del
///      catálogo Plus). Sin consumir cuota ni tocar Gemini.
///   3. Gating de terceros (<c>Import:ThirdPartyEnabled</c>, default false): <c>platform</c>
///      viaja en la QUERY STRING, así que este check corre SIEMPRE antes de leer el body —
///      platform ≠ self con el flag apagado → 403 <c>third_party_import_disabled</c> sin
///      streamear un solo byte del vídeo.
///   4. Validaciones baratas del multipart (mime allowlist + tamaño con cap durante la copia)
///      → 400 estructurado sin bufferizar el vídeo entero en memoria. Un 400 NO gasta cuota.
///   5. Cuota por usuario (30/mes + 10/día, ventanas independientes sobre <c>usage_counters</c>,
///      TOCTOU-safe). Agotada → 429 <c>import_limit_reached</c>.
///   6. Extracción. La cuota sigue la FACTURACIÓN de Gemini: éxito, "sin sitios" y cualquier
///      fallo post-2xx de generateContent (<c>Billed</c>: truncated/filtered/invalid_json) →
///      cuota consumida (Gemini cobró; el contenido del vídeo puede provocar esos fallos a
///      voluntad y reembolsarlos regalaría llamadas caras infinitas). Fallo SIN facturación
///      (upload/poll, duration_unknown, HTTP no-2xx, límites autoritativos) → REEMBOLSO.
///
/// Rate limit anti-abuso adicional: techo por IP (<c>ImportLimit</c>, 20/hr) además de la cuota
/// por usuario — un atacante con N cuentas no escala el gasto de Gemini por encima del techo de IP.
/// </summary>
[ApiController]
[Route("import")]
[Authorize]
public class ImportController : ControllerBase
{
    private const string TierPro = "pro";

    /// <summary>Cuota mensual de imports por usuario (mes natural UTC). Decisión de producto: 30/mes.</summary>
    public const int MonthlyLimit = 30;

    /// <summary>Cuota diaria de imports por usuario (día UTC). Decisión de producto: 10/día.</summary>
    public const int DailyLimit = 10;

    /// <summary>Feature key de la ventana mensual en <c>usage_counters</c> (periodo = primer día del mes UTC).</summary>
    public const string FeatureMonthly = "import_monthly";

    /// <summary>Feature key de la ventana diaria en <c>usage_counters</c> (periodo = día UTC).</summary>
    public const string FeatureDaily = "import_daily";

    /// <summary>Margen de overhead multipart (boundaries + headers de la part) sobre MaxSizeBytes
    /// para el rechazo temprano por Content-Length.</summary>
    private const int MultipartOverheadBytes = 16 * 1024;

    /// <summary>Longitud máxima de <c>creatorHandle</c> tras sanear (atribución de creador).</summary>
    private const int MaxCreatorHandleLength = 64;

    private static readonly string[] AllowedPlatforms = { "self", "tiktok", "instagram", "other" };

    private readonly VideoExtractionService _extractor;
    private readonly ImportMatchingService _matcher;
    private readonly IUsageCounterService _counters;
    private readonly LocalListDbContext _db;
    private readonly ImportOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<ImportController> _logger;

    public ImportController(
        VideoExtractionService extractor,
        ImportMatchingService matcher,
        IUsageCounterService counters,
        LocalListDbContext db,
        IOptions<ImportOptions> options,
        TimeProvider time,
        ILogger<ImportController> logger)
    {
        _extractor = extractor;
        _matcher = matcher;
        _counters = counters;
        _db = db;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Import de un vídeo propio del usuario. El multipart lleva SOLO el fichero; los metadatos
    /// <c>platform</c> (self|tiktok|instagram|other, default self) y <c>creatorHandle</c> viajan
    /// en la QUERY STRING a propósito: la query se lee ANTES del body, así el gating de terceros
    /// corre SIEMPRE pre-body — con los metadatos dentro del form, un multipart hostil que ponga
    /// el fichero primero forzaría streamear hasta 150 MB a disco para acabar en 403. Límite de
    /// tamaño 150 MB SOLO para este endpoint (el resto de la API está capado a 10 MB por Kestrel);
    /// <see cref="RequestSizeLimitAttribute"/> lo sube per-endpoint.
    /// </summary>
    [HttpPost("video")]
    [EnableRateLimiting("ImportLimit")]
    [DisableFormValueModelBinding]
    [RequestSizeLimit(157_286_400)] // 150 MB — override per-endpoint del cap global de Kestrel (10 MB)
    public async Task<IActionResult> ImportVideo(
        [FromQuery] string? platform, [FromQuery] string? creatorHandle, CancellationToken ct)
    {
        // 1. Identidad fresca (App HS256 → Guid en sub; Firebase → lookup).
        var userId = await User.GetUserIdAsync(_db, ct);
        if (userId is null)
            return Unauthorized(new { error = "Invalid token claims." });

        // 2. Gate Plus — import es feature del catálogo Plus. Tier SIEMPRE fresco de DB (el claim
        //    del JWT vive 15 min y es forjable), mismo patrón que PlanGenerationGateService.
        var tier = await _db.Users
            .Where(u => u.Id == userId.Value)
            .Select(u => u.Tier)
            .FirstOrDefaultAsync(ct);
        if (tier is null)
            return Unauthorized(new { error = "Invalid token claims." });
        if (!string.Equals(tier, TierPro, StringComparison.Ordinal))
        {
            _logger.LogInformation("Import: denied, user {UserId} not Plus (tier={Tier})", userId, tier);
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "import_requires_plus" });
        }

        // 3. Debe ser multipart/form-data con boundary.
        if (!MultipartRequestHelper.IsMultipartContentType(Request.ContentType) ||
            !TryGetBoundary(Request.ContentType, out var boundary))
            return BadRequest(new { error = "import_invalid_request", message = "multipart/form-data with a video file is required" });

        // 3b. Rechazo temprano por Content-Length: si el cuerpo entero ya supera el límite del
        //     fichero (el vídeo domina el tamaño del multipart), cortamos ANTES de leer el body.
        if (Request.ContentLength is { } declared && declared > _options.MaxSizeBytes + MultipartOverheadBytes)
            return BadRequest(new { error = "import_too_large", maxBytes = _options.MaxSizeBytes });

        // 4. Metadatos desde la QUERY STRING (leída antes del body): el gating de terceros corre
        //    SIEMPRE pre-body — un multipart hostil no puede forzarnos a streamear 150 MB a disco
        //    para acabar en 403 (con los metadatos dentro del form, el orden de las parts lo
        //    decidía el cliente).
        var normalizedPlatform = NormalizePlatform(platform);
        var sanitizedHandle = SanitizeCreatorHandle(creatorHandle);
        if (!IsPlatformAllowed(normalizedPlatform))
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "third_party_import_disabled" });

        // 5. Parse del multipart: SOLO el fichero, streaming a temp file con cap.
        string? tempPath = null;
        string? fileMime = null;
        long fileSize = 0;

        try
        {
            try
            {
                var reader = new MultipartReader(boundary, Request.Body);
                MultipartSection? section;
                while ((section = await reader.ReadNextSectionAsync(ct)) is not null)
                {
                    if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var cd))
                        continue;

                    if (!HasFileContentDisposition(cd))
                        continue; // campos de texto del form se ignoran (los metadatos van por query)

                    if (tempPath is not null)
                        continue; // ya tenemos el fichero; ignoramos ficheros extra

                    // Rechazo barato de MIME contra la allowlist ANTES de copiar un solo byte.
                    fileMime = (section.ContentType ?? string.Empty).Trim().ToLowerInvariant();
                    if (!_options.AllowedMimeTypes.Contains(fileMime))
                        return BadRequest(new { error = "import_unsupported_format", mimeType = fileMime });

                    tempPath = Path.Combine(Path.GetTempPath(), $"llimport-{Guid.NewGuid():N}.tmp");
                    await using var fs = new FileStream(
                        tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                        bufferSize: 81920, useAsync: true);
                    fileSize = await CopyWithCapAsync(section.Body, fs, _options.MaxSizeBytes, ct);
                    if (fileSize < 0)
                        return BadRequest(new { error = "import_too_large", maxBytes = _options.MaxSizeBytes });
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                // Multipart malformado/truncado (0 parts, boundary roto, body cortado): fallo del
                // CLIENTE, no nuestro → 400 estructurado, nunca un 500 opaco.
                _logger.LogInformation(ex, "Import: malformed multipart body");
                return BadRequest(new { error = "import_invalid_request", message = "malformed multipart body" });
            }

            if (tempPath is null || fileSize <= 0)
                return BadRequest(new { error = "import_missing_file", message = "a video file part is required" });

            // 6. Cuota — se consume SOLO tras pasar las validaciones baratas (mime/size/terceros).
            //    Dos ventanas independientes. Consumimos la diaria primero (más ajustada); si la
            //    mensual está al tope, REEMBOLSAMOS la diaria y rechazamos (no cobramos un slot por
            //    una request que no arranca). Consumir ANTES de llamar a Gemini acota el coste
            //    (N uploads concurrentes no se cuelan gratis contra la cuota).
            var now = _time.GetUtcNow();
            var dayStart = DateOnly.FromDateTime(now.UtcDateTime);
            var monthStart = new DateOnly(now.Year, now.Month, 1);

            if (!await _counters.TryConsumeAsync(userId.Value, FeatureDaily, dayStart, DailyLimit, ct))
                return await LimitReached(userId.Value, FeatureDaily, dayStart, DailyLimit, "daily", dayStart.AddDays(1), ct);

            if (!await _counters.TryConsumeAsync(userId.Value, FeatureMonthly, monthStart, MonthlyLimit, ct))
            {
                await _counters.ReleaseAsync(userId.Value, FeatureDaily, dayStart, ct); // devuelve el slot diario
                return await LimitReached(userId.Value, FeatureMonthly, monthStart, MonthlyLimit, "monthly", monthStart.AddMonths(1), ct);
            }

            // 7. Extracción. El servicio sube a Gemini, extrae, sanea y borra el fichero remoto.
            try
            {
                await using var videoStream = new FileStream(
                    tempPath, FileMode.Open, FileAccess.Read, FileShare.None,
                    bufferSize: 81920, useAsync: true);

                var result = await _extractor.ExtractAsync(
                    videoStream, fileSize, fileMime!, normalizedPlatform, caption: null, ct);

                // T3 — matching contra el catálogo curado de la ciudad detectada (una query, en
                // memoria). No crea places (curación = admin); los no matcheados salen igual.
                var matched = await _matcher.MatchAsync(result.City, result.Places, ct);
                var numMatched = matched.Count(m => m.MatchedPlaceId is not null);

                // Anota la calidad del matching en la MISMA fila de diagnóstico escrita durante la
                // extracción (un UPDATE por PK, fuera del camino caliente de Gemini). Best-effort:
                // un fallo de métrica jamás debe tumbar el import.
                if (result.MetricId is { } metricId)
                {
                    try
                    {
                        await _db.VideoImportMetrics
                            .Where(m => m.Id == metricId)
                            .ExecuteUpdateAsync(s => s.SetProperty(m => m.NumMatched, numMatched), ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Import: failed to persist num_matched for metric {MetricId}", metricId);
                    }
                }

                _logger.LogInformation(
                    "Import: ok user={UserId} platform={Platform} places={N} matched={M} city={City}",
                    userId, normalizedPlatform, result.Places.Count, numMatched, result.City ?? "(null)");

                return Ok(ImportVideoResponseMapper.From(result, matched, normalizedPlatform, sanitizedHandle));
            }
            catch (NoPlacesFoundException)
            {
                // Gemini SÍ procesó el vídeo (coste pagado) y no halló sitios → la cuota se
                // mantiene consumida (misma semántica que el gate de generación con "sin places").
                return StatusCode(StatusCodes.Status422UnprocessableEntity, new { error = "no_places_found" });
            }
            catch (VideoExtractionException ex)
            {
                // Reembolso condicionado a la FACTURACIÓN, no al valor entregado:
                //   - Billed (truncated/MAX_TOKENS, content_filtered_*, invalid_json — todos
                //     post-2xx de generateContent): Gemini YA cobró los ~tokens del vídeo, y el
                //     CONTENIDO del vídeo puede provocar esos fallos a voluntad. Reembolsar aquí
                //     regalaría llamadas caras ilimitadas contra cuota 0 (solo acotadas por el
                //     techo por IP) → la cuota SE MANTIENE, como en no_places_found.
                //   - No facturado (upload/poll fallido, duration_unknown, HTTP no-2xx de
                //     generate, o rechazo pre-generate como VideoTooLong/VideoTooLarge del
                //     tamaño/duración autoritativos): sin coste de generateContent → REEMBOLSAMOS
                //     ambas ventanas (el usuario no obtuvo valor y no hubo gasto que proteger).
                var billed = ex is ExtractionUnavailableException { Billed: true };
                if (!billed)
                {
                    await _counters.ReleaseAsync(userId.Value, FeatureDaily, dayStart, ct);
                    await _counters.ReleaseAsync(userId.Value, FeatureMonthly, monthStart, ct);
                }
                return MapExtractionError(ex);
            }
        }
        finally
        {
            // Borra el temp file local SIEMPRE (éxito, error o cancelación). El fichero remoto de
            // Gemini lo borra el propio VideoExtractionService en su finally.
            if (tempPath is not null) TryDeleteTemp(tempPath);
        }
    }

    private async Task<IActionResult> LimitReached(
        Guid userId, string feature, DateOnly periodStart, int limit, string window,
        DateOnly resetDay, CancellationToken ct)
    {
        var used = await _counters.GetUsedAsync(userId, feature, periodStart, ct);
        var resetsAt = new DateTimeOffset(resetDay.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        _logger.LogInformation(
            "Import: {Window} quota reached user={UserId} used={Used}/{Limit}", window, userId, used, limit);
        // 429 (no 403): el caller YA es Plus, no hay upsell que pintar — es throttling antiabuso,
        // mismo criterio que el cap diario Plus del gate de generación (daily_cap_reached).
        return StatusCode(StatusCodes.Status429TooManyRequests, new
        {
            error = "import_limit_reached",
            window,
            used,
            limit,
            resetsAt,
        });
    }

    private IActionResult MapExtractionError(VideoExtractionException ex) => ex switch
    {
        // El servicio revalida contra la metadata autoritativa del File API; estos casos son
        // defensa en profundidad sobre las validaciones baratas del endpoint.
        VideoUnsupportedFormatException => BadRequest(new { error = "import_unsupported_format" }),
        VideoTooLargeException tooLarge => BadRequest(new { error = "import_too_large", maxBytes = tooLarge.MaxBytes }),
        VideoTooLongException tooLong => BadRequest(new { error = "import_video_too_long", maxSeconds = tooLong.MaxSec }),
        // Fallo de infraestructura (Gemini caído, File API, truncado…). El usuario reintenta.
        ExtractionUnavailableException => StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "import_unavailable" }),
        _ => StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = "import_unavailable" }),
    };

    private bool IsPlatformAllowed(string platform) =>
        string.Equals(platform, "self", StringComparison.Ordinal) || _options.ThirdPartyEnabled;

    private static string NormalizePlatform(string? raw)
    {
        var p = (raw ?? string.Empty).Trim().ToLowerInvariant();
        return AllowedPlatforms.Contains(p) ? p : "other";
    }

    /// <summary>Handle de creador saneado: quita control/ángulos, recorta, sin URLs. Atribución inerte.</summary>
    private static string? SanitizeCreatorHandle(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        // Reutilizamos el mismo sanitizador de salida del slice Chat que usa el import (quita
        // URLs, markdown/HTML, escapa ángulos): el handle acaba pintado en un plan.
        var s = LocalList.API.NET.Features.Chat.Services.OutputSanitizer.Sanitize(raw).Trim();
        if (s.Length > MaxCreatorHandleLength) s = s[..MaxCreatorHandleLength].TrimEnd();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    /// <summary>
    /// Copia <paramref name="source"/> en <paramref name="dest"/> abortando en cuanto se supera
    /// <paramref name="capBytes"/>. Devuelve los bytes copiados, o -1 si excedió el cap (el temp
    /// file queda parcial y se borra en el finally del caller). Nunca bufferiza el vídeo entero.
    /// </summary>
    private static async Task<long> CopyWithCapAsync(Stream source, Stream dest, long capBytes, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > capBytes) return -1;
            await dest.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return total;
    }

    private void TryDeleteTemp(string path)
    {
        try
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Import: failed to delete temp file {Path}", path);
        }
    }

    private static bool TryGetBoundary(string? contentType, out string boundary)
    {
        boundary = string.Empty;
        if (!MediaTypeHeaderValue.TryParse(contentType, out var mediaType)) return false;
        var value = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
        if (string.IsNullOrEmpty(value) || value.Length > 256) return false;
        boundary = value;
        return true;
    }

    private static bool HasFileContentDisposition(ContentDispositionHeaderValue cd) =>
        cd.DispositionType.Equals("form-data") &&
        (!string.IsNullOrEmpty(cd.FileName.Value) || !string.IsNullOrEmpty(cd.FileNameStar.Value));
}

/// <summary>Detección barata de multipart/form-data (evita depender del binding de formularios).</summary>
internal static class MultipartRequestHelper
{
    public static bool IsMultipartContentType(string? contentType) =>
        !string.IsNullOrEmpty(contentType) &&
        contentType.Contains("multipart/", StringComparison.OrdinalIgnoreCase);
}
