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
///   3. Validaciones baratas del multipart (mime allowlist + tamaño) → 400 estructurado ANTES
///      de bufferizar el vídeo entero. Un 400 NO gasta cuota.
///   4. Gating de terceros (<c>Import:ThirdPartyEnabled</c>, default false): platform ≠ self con
///      el flag apagado → 403 <c>third_party_import_disabled</c>. Antes de consumir cuota.
///   5. Cuota por usuario (30/mes + 10/día, ventanas independientes sobre <c>usage_counters</c>,
///      TOCTOU-safe). Agotada → 429 <c>import_limit_reached</c>.
///   6. Extracción. Éxito o "sin sitios" (Gemini pagó) → cuota consumida; fallo de infra o vídeo
///      no viable (sin valor) → cuota REEMBOLSADA.
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

    /// <summary>Máximo de bytes que aceptamos leer de un campo de texto del form (platform/creatorHandle).</summary>
    private const int MaxTextFieldBytes = 4096;

    /// <summary>Longitud máxima de <c>creatorHandle</c> tras sanear (atribución de creador).</summary>
    private const int MaxCreatorHandleLength = 64;

    private static readonly string[] AllowedPlatforms = { "self", "tiktok", "instagram", "other" };

    private readonly VideoExtractionService _extractor;
    private readonly IUsageCounterService _counters;
    private readonly LocalListDbContext _db;
    private readonly ImportOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<ImportController> _logger;

    public ImportController(
        VideoExtractionService extractor,
        IUsageCounterService counters,
        LocalListDbContext db,
        IOptions<ImportOptions> options,
        TimeProvider time,
        ILogger<ImportController> logger)
    {
        _extractor = extractor;
        _counters = counters;
        _db = db;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Import de un vídeo propio del usuario. Multipart: campo de fichero (<c>video</c>/<c>file</c>)
    /// + campos opcionales <c>platform</c> (self|tiktok|instagram|other, default self) y
    /// <c>creatorHandle</c>. Límite de tamaño 150 MB SOLO para este endpoint (el resto de la API
    /// está capado a 10 MB por Kestrel); <see cref="RequestSizeLimitAttribute"/> lo sube per-endpoint.
    /// </summary>
    [HttpPost("video")]
    [EnableRateLimiting("ImportLimit")]
    [DisableFormValueModelBinding]
    [RequestSizeLimit(157_286_400)] // 150 MB — override per-endpoint del cap global de Kestrel (10 MB)
    public async Task<IActionResult> ImportVideo(CancellationToken ct)
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
        if (Request.ContentLength is { } declared && declared > _options.MaxSizeBytes + MaxTextFieldBytes * 4)
            return BadRequest(new { error = "import_too_large", maxBytes = _options.MaxSizeBytes });

        // 4. Parse del multipart: campos de texto + fichero a temp file (streaming, con cap).
        var platform = "self";
        string? creatorHandle = null;
        string? tempPath = null;
        string? fileMime = null;
        long fileSize = 0;

        try
        {
            var reader = new MultipartReader(boundary, Request.Body);
            MultipartSection? section;
            while ((section = await reader.ReadNextSectionAsync(ct)) is not null)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var cd))
                    continue;

                if (HasFileContentDisposition(cd))
                {
                    if (tempPath is not null)
                        continue; // ya tenemos el fichero; ignoramos ficheros extra

                    // Rechazo barato de MIME contra la allowlist ANTES de copiar un solo byte.
                    fileMime = (section.ContentType ?? string.Empty).Trim().ToLowerInvariant();
                    if (!_options.AllowedMimeTypes.Contains(fileMime))
                        return BadRequest(new { error = "import_unsupported_format", mimeType = fileMime });

                    // Si ya sabemos que es un import de terceros deshabilitado (platform vino antes
                    // del fichero), rechazamos sin escribir 150 MB a disco.
                    if (!IsPlatformAllowed(platform))
                        return StatusCode(StatusCodes.Status403Forbidden, new { error = "third_party_import_disabled" });

                    tempPath = Path.Combine(Path.GetTempPath(), $"llimport-{Guid.NewGuid():N}.tmp");
                    await using var fs = new FileStream(
                        tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                        bufferSize: 81920, useAsync: true);
                    fileSize = await CopyWithCapAsync(section.Body, fs, _options.MaxSizeBytes, ct);
                    if (fileSize < 0)
                        return BadRequest(new { error = "import_too_large", maxBytes = _options.MaxSizeBytes });
                }
                else if (HasFormFieldContentDisposition(cd, out var name))
                {
                    var value = await ReadTextFieldAsync(section, ct);
                    if (string.Equals(name, "platform", StringComparison.OrdinalIgnoreCase))
                        platform = NormalizePlatform(value);
                    else if (string.Equals(name, "creatorHandle", StringComparison.OrdinalIgnoreCase))
                        creatorHandle = SanitizeCreatorHandle(value);
                }
            }

            if (tempPath is null || fileSize <= 0)
                return BadRequest(new { error = "import_missing_file", message = "a video file part is required" });

            // 4b. Gating de terceros (si el fichero llegó antes que el campo platform, este es el
            //     punto donde lo cazamos). Antes de consumir cuota.
            if (!IsPlatformAllowed(platform))
                return StatusCode(StatusCodes.Status403Forbidden, new { error = "third_party_import_disabled" });

            // 5. Cuota — se consume SOLO tras pasar las validaciones baratas (mime/size/terceros).
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

            // 6. Extracción. El servicio sube a Gemini, extrae, sanea y borra el fichero remoto.
            try
            {
                await using var videoStream = new FileStream(
                    tempPath, FileMode.Open, FileAccess.Read, FileShare.None,
                    bufferSize: 81920, useAsync: true);

                var result = await _extractor.ExtractAsync(videoStream, fileSize, fileMime!, platform, caption: null, ct);

                _logger.LogInformation(
                    "Import: ok user={UserId} platform={Platform} places={N} city={City}",
                    userId, platform, result.Places.Count, result.City ?? "(null)");

                return Ok(ImportVideoResponseMapper.From(result, platform, creatorHandle));
            }
            catch (NoPlacesFoundException)
            {
                // Gemini SÍ procesó el vídeo (coste pagado) y no halló sitios → la cuota se
                // mantiene consumida (misma semántica que el gate de generación con "sin places").
                return StatusCode(StatusCodes.Status422UnprocessableEntity, new { error = "no_places_found" });
            }
            catch (VideoExtractionException ex)
            {
                // Sin valor entregado (vídeo no viable o fallo de infra) y sin coste de
                // generateContent facturable → REEMBOLSAMOS ambas ventanas. Decisión: el usuario
                // no obtuvo su plan, cobrarle la cuota castigaría un fallo que no es suyo.
                await _counters.ReleaseAsync(userId.Value, FeatureDaily, dayStart, ct);
                await _counters.ReleaseAsync(userId.Value, FeatureMonthly, monthStart, ct);
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

    /// <summary>
    /// Lee un campo de texto del multipart directamente de <c>section.Body</c> (NO con
    /// <see cref="StreamReader"/>, que hace read-ahead y se comería el boundary corrompiendo al
    /// <see cref="MultipartReader"/>). Acota lo retenido a <see cref="MaxTextFieldBytes"/> pero
    /// drena el resto para dejar el stream alineado en el siguiente boundary.
    /// </summary>
    private static async Task<string> ReadTextFieldAsync(MultipartSection section, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[1024];
        int read;
        while ((read = await section.Body.ReadAsync(buffer, ct)) > 0)
        {
            var remaining = MaxTextFieldBytes - (int)ms.Length;
            if (remaining > 0) ms.Write(buffer, 0, Math.Min(read, remaining));
            // si ya llegamos al cap seguimos leyendo (drenando) pero sin retener más
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
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

    private static bool HasFormFieldContentDisposition(ContentDispositionHeaderValue cd, out string name)
    {
        name = HeaderUtilities.RemoveQuotes(cd.Name).Value ?? string.Empty;
        return cd.DispositionType.Equals("form-data") &&
               string.IsNullOrEmpty(cd.FileName.Value) &&
               string.IsNullOrEmpty(cd.FileNameStar.Value) &&
               !string.IsNullOrEmpty(name);
    }
}

/// <summary>Detección barata de multipart/form-data (evita depender del binding de formularios).</summary>
internal static class MultipartRequestHelper
{
    public static bool IsMultipartContentType(string? contentType) =>
        !string.IsNullOrEmpty(contentType) &&
        contentType.Contains("multipart/", StringComparison.OrdinalIgnoreCase);
}
