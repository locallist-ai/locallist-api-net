using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace LocalList.API.NET.Shared.Photos;

/// <summary>
/// Implementación real de <see cref="IR2ObjectStore"/> sobre Cloudflare R2 vía la S3 API
/// (AWSSDK.S3 apuntando al endpoint de la cuenta). El cliente se construye lazy: sin
/// credenciales nunca se instancia y <see cref="IsConfigured"/> devuelve false.
/// </summary>
public sealed class R2ObjectStore : IR2ObjectStore, IDisposable
{
    private readonly R2Options _options;
    private readonly Lazy<IAmazonS3> _client;

    public R2ObjectStore(IOptions<R2Options> options)
    {
        _options = options.Value;
        _client = new Lazy<IAmazonS3>(CreateClient);
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_options.AccountId) &&
        !string.IsNullOrWhiteSpace(_options.AccessKeyId) &&
        !string.IsNullOrWhiteSpace(_options.SecretAccessKey) &&
        !string.IsNullOrWhiteSpace(_options.Bucket) &&
        !string.IsNullOrWhiteSpace(_options.PublicUrl);

    private IAmazonS3 CreateClient() => new AmazonS3Client(
        new BasicAWSCredentials(_options.AccessKeyId, _options.SecretAccessKey),
        new AmazonS3Config
        {
            ServiceURL = $"https://{_options.AccountId}.r2.cloudflarestorage.com",
            ForcePathStyle = true,
            AuthenticationRegion = "auto",
            // Timeouts agresivos: el rehost corre inline en la creación de places y bajo el
            // proxy de Railway (40s). El default del SDK (100s × reintentos) dejaría un R2
            // colgado reteniendo la request hasta que el proxy la mata — el place NO se
            // crearía. Con 10s + 1 retry el peor caso cabe en el presupuesto y el fallo
            // degrada a "sin foto" en PhotoRehostService (nunca aborta la creación).
            Timeout = TimeSpan.FromSeconds(10),
            MaxErrorRetry = 1,
            // R2 no soporta los checksums CRC por defecto del SDK v4 en todos los paths;
            // WHEN_REQUIRED replica el comportamiento clásico compatible con R2.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        });

    public async Task UploadAsync(string key, byte[] content, string contentType, CancellationToken ct)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("R2 credentials not configured.");

        using var stream = new MemoryStream(content);
        var request = new PutObjectRequest
        {
            BucketName = _options.Bucket,
            Key = key,
            InputStream = stream,
            ContentType = contentType,
            // aws-chunked no está soportado uniformemente en R2 — content-length clásico.
            UseChunkEncoding = false,
        };
        // Mismo cache policy que el pipeline manual (Scripts/upload-photos-to-r2.js):
        // los keys son deterministas e inmutables por contenido de origen.
        request.Headers.CacheControl = "public, max-age=31536000, immutable";

        await _client.Value.PutObjectAsync(request, ct);
    }

    public void Dispose()
    {
        if (_client.IsValueCreated) _client.Value.Dispose();
    }
}
