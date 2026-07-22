using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace LocalList.API.NET.Shared.Photos;

/// <summary>
/// Rehost server-side de fotos a R2: descarga → reencode webp (máx 1200px de ancho,
/// mismo pipeline que Scripts/upload-photos-to-r2.js) → upload S3 API → URL pública.
/// Mata tres pájaros: coste marginal del SKU Place Photos de Google, exposición de la
/// API key en URLs persistidas, y hotlinks frágiles a terceros (wanderlog).
/// </summary>
public class PhotoRehostService : IPhotoRehostService
{
    public const int MaxWidthPx = 1200;
    public const int WebpQuality = 80;
    public const long MaxDownloadBytes = 20 * 1024 * 1024; // 20 MB por foto

    /// <summary>
    /// Tope de dimensiones de la fuente ANTES de decodificar (defensa decompression bomb:
    /// un PNG/webp diminuto puede declarar 60000×60000 y reventar la memoria en Image.Load).
    /// Se valida con Image.Identify (solo lee el header). Google sirve máx ~4800px.
    /// </summary>
    public const int MaxSourceDimensionPx = 8192;

    private static readonly Regex NonSlugChars = new("[^a-z0-9]+", RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly IR2ObjectStore _store;
    private readonly R2Options _options;
    private readonly ILogger<PhotoRehostService> _logger;

    public PhotoRehostService(
        HttpClient http,
        IR2ObjectStore store,
        IOptions<R2Options> options,
        ILogger<PhotoRehostService> logger)
    {
        _http = http;
        _store = store;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsConfigured => _store.IsConfigured;

    public string? PublicHost =>
        Uri.TryCreate(_options.PublicUrl, UriKind.Absolute, out var uri) ? uri.Host : null;

    public async Task<PhotoRehostResult> RehostAsync(string sourceUrl, string keyHint, CancellationToken ct)
    {
        if (PhotoUrls.DomainBucket(sourceUrl, PublicHost) == PhotoUrls.BucketR2)
            return new(PhotoRehostOutcome.AlreadyHosted, sourceUrl);

        if (!IsConfigured)
        {
            _logger.LogWarning(
                "R2 not configured — photo rehost skipped, keeping original URL (host {Host})",
                Uri.TryCreate(sourceUrl, UriKind.Absolute, out var u) ? u.Host : "invalid");
            return new(PhotoRehostOutcome.NotConfigured, sourceUrl);
        }

        // Defensa SSRF: el GET server-side solo puede apuntar a fuentes de imagen conocidas
        // (https, sin IP literals, host en la allowlist). Input admin != input de confianza.
        if (!PhotoUrls.IsAllowedSource(sourceUrl, _options.AllowedPhotoSourceHosts))
        {
            _logger.LogWarning(
                "Photo rehost blocked — source host not allowlisted (host {Host}, keyHint '{KeyHint}')",
                Uri.TryCreate(sourceUrl, UriKind.Absolute, out var bu) ? bu.Host : "invalid", keyHint);
            return new(PhotoRehostOutcome.Failed, sourceUrl, PhotoRehostFailureStage.Blocked);
        }

        var stage = PhotoRehostFailureStage.Download;
        try
        {
            var raw = await DownloadAsync(sourceUrl, ct);

            stage = PhotoRehostFailureStage.Decode;
            var webp = Reencode(raw);
            var key = BuildObjectKey(keyHint, sourceUrl);

            stage = PhotoRehostFailureStage.Upload;
            await _store.UploadAsync(key, webp, "image/webp", ct);

            var publicUrl = $"{_options.PublicUrl.TrimEnd('/')}/{key}";
            _logger.LogInformation(
                "Photo rehosted to R2: {Key} ({RawKb}KB -> {WebpKb}KB)",
                key, raw.Length / 1024, webp.Length / 1024);
            return new(PhotoRehostOutcome.Rehosted, publicUrl);
        }
        // Un timeout interno del HttpClient o del cliente S3 llega como OperationCanceledException
        // SIN que el ct del caller esté cancelado. Nunca debe propagar: un R2/una fuente colgada
        // no puede tumbar la creación de places (degrada a Failed). La cancelación real del
        // caller (proxy/cliente abortó) sí propaga.
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex,
                "Photo rehost timed out at stage {Stage} for host {Host} (keyHint '{KeyHint}')",
                stage, Uri.TryCreate(sourceUrl, UriKind.Absolute, out var u) ? u.Host : "invalid", keyHint);
            return new(PhotoRehostOutcome.Failed, sourceUrl, stage);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // La URL con key nunca se loguea entera — solo el host (política PiiRedactor/keys).
            _logger.LogWarning(ex,
                "Photo rehost failed at stage {Stage} for host {Host} (keyHint '{KeyHint}')",
                stage, Uri.TryCreate(sourceUrl, UriKind.Absolute, out var u) ? u.Host : "invalid", keyHint);
            return new(PhotoRehostOutcome.Failed, sourceUrl, stage);
        }
    }

    /// <summary>
    /// Descarga validando content-type (debe ser image/* si el server lo declara) y tamaño
    /// (Content-Length y conteo real de bytes, ambos contra <see cref="MaxDownloadBytes"/> —
    /// el header puede mentir o faltar).
    /// </summary>
    private async Task<byte[]> DownloadAsync(string sourceUrl, CancellationToken ct)
    {
        using var response = await _http.GetAsync(
            sourceUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null &&
            !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) &&
            !mediaType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Photo source returned non-image content-type '{mediaType}'.");
        }

        if (response.Content.Headers.ContentLength is > MaxDownloadBytes)
            throw new InvalidOperationException($"Photo exceeds {MaxDownloadBytes} bytes (Content-Length).");

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxDownloadBytes)
                throw new InvalidOperationException($"Photo exceeds {MaxDownloadBytes} bytes.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    public async Task<List<string>?> RehostForIngestAsync(
        IReadOnlyList<string>? urls, string keyHint, CancellationToken ct, IngestPhotoBreaker? breaker = null)
    {
        if (urls is not { Count: > 0 }) return null;

        var result = new List<string>(urls.Count);
        var degradeRest = false;
        foreach (var url in urls)
        {
            if (string.IsNullOrWhiteSpace(url)) continue;

            // G2: circuit breaker de ingesta. Dejamos de intentar rehost (degradamos a "sin
            // foto") cuando el breaker está abierto — 3 uploads consecutivos fallidos O el
            // presupuesto de wall-clock de la request agotado (ronda 3: acotar INTENTOS no basta,
            // un R2 colgado agota el proxy de Railway antes del 3er fallo) — o cuando un cuelgue
            // ya consumió el presupuesto de ESTA llamada. NO re-descargamos de Google (no
            // re-facturar el SKU Place Photos contra un R2 caído): la URL con key se descarta, la
            // sin key se conserva para el backfill. Nunca se aborta la creación por un R2 colgado.
            if (degradeRest || breaker is { IsOpen: true })
            {
                if (PhotoUrls.ContainsApiKey(url)) continue;
                result.Add(url);
                continue;
            }

            // Acotamos ESTE upload al presupuesto restante de la request (linked CTS): un R2
            // colgado no puede consumir más allá del presupuesto agregado, y al cortarlo queda
            // margen para el commit del place ANTES de que el proxy cancele el ct del caller.
            using var attemptCts = breaker is not null
                ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                : null;
            attemptCts?.CancelAfter(breaker!.Remaining);
            var attemptCt = attemptCts?.Token ?? ct;

            PhotoRehostResult rehosted;
            try
            {
                rehosted = await RehostAsync(url, keyHint, attemptCt);
            }
            // El presupuesto de wall-clock (linked CTS) canceló el upload SIN que el ct del
            // caller esté cancelado: invariante (d) — un R2 colgado que agota el presupuesto
            // JAMÁS tumba la creación. Contamos el fallo de upload, degradamos a "sin foto" y
            // dejamos de intentar el resto. Una cancelación REAL del caller (proxy/cliente
            // abortó; ct cancelado) sí propaga — el import por chunks decide qué conserva.
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "Photo rehost budget exhausted (hung R2) — degrading to no-photo for keyHint '{KeyHint}'",
                    keyHint);
                breaker?.RecordUploadFailure();
                // Latch el presupuesto: el resto de places de la request (llamadas separadas a
                // este método) degradan de inmediato y de forma determinista vía breaker.IsOpen,
                // sin depender del borde exacto del reloj.
                breaker?.TripBudget();
                degradeRest = true;
                if (PhotoUrls.ContainsApiKey(url)) continue;
                result.Add(url);
                continue;
            }

            if (rehosted.Outcome == PhotoRehostOutcome.Rehosted)
                breaker?.RecordUploadSuccess();
            else if (rehosted.FailureStage == PhotoRehostFailureStage.Upload)
                breaker?.RecordUploadFailure();

            if (rehosted.Outcome == PhotoRehostOutcome.Failed && PhotoUrls.ContainsApiKey(url))
            {
                // Con R2 configurado, una URL de Google con key JAMÁS se persiste:
                // mejor sin foto que facturando + exponiendo la key en cada render.
                continue;
            }
            result.Add(rehosted.Url);
        }

        return result.Count > 0 ? result : null;
    }

    private static byte[] Reencode(byte[] raw)
    {
        // Decompression bomb: Identify solo lee el header — rechaza dimensiones absurdas
        // ANTES de materializar el bitmap completo en memoria con Image.Load.
        //
        // g4 (presupuesto de decodificación, aceptado y documentado): el cap de 8192px acota
        // el peor caso a 8192²×4 ≈ 256 MB de bitmap por imagen aceptada. NO se baja a 4096px
        // (64 MB) a propósito: Google Places sirve fotos de hasta ~4800px y googleusercontent
        // (fotos de usuario) aún mayores — un cap de 4096 rechazaría fuentes LEGÍTIMAS, no
        // solo bombs. ImageSharp 3.x no expone un límite de píxeles en DecoderOptions, así que
        // este check de header ES la defensa. La presión de memoria real está acotada además
        // por (a) MaxDownloadBytes=20 MB (una bomb de 8192px comprimida no cabe salvo formatos
        // extremos, que MaxFrames=1 + el reencode inmediato mitigan) y (b) el downscale a
        // 1200px justo después. Si esto se volviera problema bajo alta concurrencia de rehosts
        // inline, la palanca es un MemoryAllocator con presupuesto en Configuration, no bajar
        // el cap. Detalle en Shared/Photos/README.md.
        var info = Image.Identify(raw);
        if (info.Width > MaxSourceDimensionPx || info.Height > MaxSourceDimensionPx)
            throw new InvalidOperationException(
                $"Source image {info.Width}x{info.Height} exceeds {MaxSourceDimensionPx}px limit.");

        var decoderOptions = new SixLabors.ImageSharp.Formats.DecoderOptions { MaxFrames = 1 };
        using var image = Image.Load(decoderOptions, raw);
        if (image.Width > MaxWidthPx)
        {
            // Height 0 = mantener aspect ratio (equivalente a withoutEnlargement del script JS:
            // solo redimensionamos hacia abajo).
            image.Mutate(x => x.Resize(MaxWidthPx, 0));
        }

        using var output = new MemoryStream();
        image.SaveAsWebp(output, new WebpEncoder { Quality = WebpQuality });
        return output.ToArray();
    }

    /// <summary>
    /// Key determinista: <c>places/{slug}-{hash8}.webp</c>. El hash se calcula sobre
    /// scheme+host+path (sin query) para que una rotación de la API key de Google no
    /// cambie el key — re-ejecutar el backfill sobreescribe el mismo objeto (idempotente).
    /// Decisión (m5): dos URLs que solo difieren en la query colapsan al mismo key; aceptable
    /// porque en las fuentes allowlisted la identidad de la imagen vive en el path (Google
    /// photo ref, wanderlog). Si se allowlista una fuente query-based, añadir aquí un
    /// discriminador de query. Detalle en Shared/Photos/README.md.
    /// </summary>
    internal static string BuildObjectKey(string keyHint, string sourceUrl)
    {
        var slug = Slugify(keyHint);
        if (string.IsNullOrEmpty(slug)) slug = "photo";

        var canonical = Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri)
            ? $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}"
            : sourceUrl;
        var hash8 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..8]
            .ToLowerInvariant();

        return $"places/{slug}-{hash8}.webp";
    }

    // Port del slugify de Scripts/upload-photos-to-r2.js.
    internal static string Slugify(string name) =>
        NonSlugChars.Replace(
                name.ToLowerInvariant()
                    .Replace("'", string.Empty)
                    .Replace("’", string.Empty)
                    .Replace("&", "and"),
                "-")
            .Trim('-');
}
