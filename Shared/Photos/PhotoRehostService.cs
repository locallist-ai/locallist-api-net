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

        try
        {
            var raw = await _http.GetByteArrayAsync(sourceUrl, ct);
            if (raw.LongLength > MaxDownloadBytes)
                throw new InvalidOperationException($"Photo exceeds {MaxDownloadBytes} bytes.");

            var webp = Reencode(raw);
            var key = BuildObjectKey(keyHint, sourceUrl);
            await _store.UploadAsync(key, webp, "image/webp", ct);

            var publicUrl = $"{_options.PublicUrl.TrimEnd('/')}/{key}";
            _logger.LogInformation(
                "Photo rehosted to R2: {Key} ({RawKb}KB -> {WebpKb}KB)",
                key, raw.Length / 1024, webp.Length / 1024);
            return new(PhotoRehostOutcome.Rehosted, publicUrl);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // La URL con key nunca se loguea entera — solo el host (política PiiRedactor/keys).
            _logger.LogWarning(ex,
                "Photo rehost failed for host {Host} (keyHint '{KeyHint}')",
                Uri.TryCreate(sourceUrl, UriKind.Absolute, out var u) ? u.Host : "invalid", keyHint);
            return new(PhotoRehostOutcome.Failed, sourceUrl);
        }
    }

    public async Task<List<string>?> RehostForIngestAsync(
        IReadOnlyList<string>? urls, string keyHint, CancellationToken ct)
    {
        if (urls is not { Count: > 0 }) return null;

        var result = new List<string>(urls.Count);
        foreach (var url in urls)
        {
            if (string.IsNullOrWhiteSpace(url)) continue;

            var rehosted = await RehostAsync(url, keyHint, ct);
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
        using var image = Image.Load(raw);
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
