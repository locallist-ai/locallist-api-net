using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using LocalList.API.NET.Shared.Photos;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;

namespace LocalList.API.Tests.Unit;

/// <summary>
/// Unit tests de <see cref="PhotoRehostService"/> con el cliente R2 mockeado
/// (permitido por política; la DB nunca se mockea) y las descargas interceptadas
/// por <see cref="FakePhotoHandler"/>.
/// </summary>
public class PhotoRehostServiceTests
{
    private const string PublicUrl = "https://pub-test.r2.dev";
    private const string GoogleUrl =
        "https://places.googleapis.com/v1/places/abc/photos/def/media?maxWidthPx=1600&key=SECRET-KEY";
    private const string WanderlogUrl = "https://wanderlog.com/photos/some-photo.jpg";

    private static PhotoRehostService CreateService(
        FakeR2ObjectStore store, FakePhotoHandler? handler = null, string publicUrl = PublicUrl)
    {
        var options = Options.Create(new R2Options
        {
            AccountId = "test-account",
            AccessKeyId = "test-key",
            SecretAccessKey = "test-secret",
            Bucket = "locallist-images",
            PublicUrl = publicUrl,
        });
        return new PhotoRehostService(
            new HttpClient(handler ?? new FakePhotoHandler()),
            store, options, NullLogger<PhotoRehostService>.Instance);
    }

    [Fact]
    public async Task Rehost_WithoutConfig_KeepsOriginalUrl_AndUploadsNothing()
    {
        var store = new FakeR2ObjectStore(); // Configured = false
        var handler = new FakePhotoHandler();
        var service = CreateService(store, handler);

        var result = await service.RehostAsync(GoogleUrl, "Test Place", TestContext.Current.CancellationToken);

        Assert.Equal(PhotoRehostOutcome.NotConfigured, result.Outcome);
        Assert.Equal(GoogleUrl, result.Url);
        Assert.Empty(store.Objects);
        Assert.Empty(handler.Calls); // ni siquiera descarga
    }

    [Fact]
    public async Task Rehost_GoogleUrl_UploadsWebpMax1200_AndReturnsPublicUrl()
    {
        var store = new FakeR2ObjectStore { Configured = true };
        var service = CreateService(store); // default: JPEG 1600x900

        var result = await service.RehostAsync(GoogleUrl, "Broken Shaker", TestContext.Current.CancellationToken);

        Assert.Equal(PhotoRehostOutcome.Rehosted, result.Outcome);
        Assert.StartsWith($"{PublicUrl}/places/broken-shaker-", result.Url);
        Assert.EndsWith(".webp", result.Url);
        Assert.DoesNotContain("key=", result.Url);

        var (key, bytes) = Assert.Single(store.Objects);
        Assert.Equal(result.Url, $"{PublicUrl}/{key}");

        // El objeto subido es webp real y el ancho quedó cap-eado a 1200 (fuente 1600).
        var format = Image.DetectFormat(bytes);
        Assert.IsType<WebpFormat>(format, exactMatch: false);
        var info = Image.Identify(bytes);
        Assert.Equal(1200, info.Width);
        Assert.Equal(675, info.Height); // 900 * (1200/1600) — mantiene aspect ratio
    }

    [Fact]
    public async Task Rehost_SmallImage_DoesNotUpscale()
    {
        var store = new FakeR2ObjectStore { Configured = true };
        var handler = new FakePhotoHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(FakePhotoHandler.CreateJpeg(800, 600))
            }
        };
        var service = CreateService(store, handler);

        var result = await service.RehostAsync(WanderlogUrl, "Small Pic", TestContext.Current.CancellationToken);

        Assert.Equal(PhotoRehostOutcome.Rehosted, result.Outcome);
        var info = Image.Identify(store.Objects.Values.Single());
        Assert.Equal(800, info.Width); // withoutEnlargement: nunca se agranda
    }

    [Fact]
    public async Task Rehost_UrlAlreadyOnR2_IsUntouched()
    {
        var store = new FakeR2ObjectStore { Configured = true };
        var handler = new FakePhotoHandler();
        var service = CreateService(store, handler);
        var hosted = $"{PublicUrl}/places/existing-abcd1234.webp";

        var result = await service.RehostAsync(hosted, "Existing", TestContext.Current.CancellationToken);

        Assert.Equal(PhotoRehostOutcome.AlreadyHosted, result.Outcome);
        Assert.Equal(hosted, result.Url);
        Assert.Empty(store.Objects);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task Rehost_DownloadFailure_ReturnsFailedWithOriginalUrl()
    {
        var store = new FakeR2ObjectStore { Configured = true };
        var handler = new FakePhotoHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        };
        var service = CreateService(store, handler);

        var result = await service.RehostAsync(WanderlogUrl, "Gone", TestContext.Current.CancellationToken);

        Assert.Equal(PhotoRehostOutcome.Failed, result.Outcome);
        Assert.Equal(WanderlogUrl, result.Url);
        Assert.Empty(store.Objects);
    }

    [Fact]
    public async Task Rehost_CorruptImageBytes_ReturnsFailed()
    {
        var store = new FakeR2ObjectStore { Configured = true };
        var handler = new FakePhotoHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 0x00, 0x01, 0x02, 0x03 })
            }
        };
        var service = CreateService(store, handler);

        var result = await service.RehostAsync(GoogleUrl, "Corrupt", TestContext.Current.CancellationToken);

        Assert.Equal(PhotoRehostOutcome.Failed, result.Outcome);
        Assert.Empty(store.Objects);
    }

    [Fact]
    public async Task RehostForIngest_ConfiguredAndGoogleFails_DropsThePhoto()
    {
        // Con R2 configurado, una URL de Google con key JAMÁS se persiste tras un fallo.
        var store = new FakeR2ObjectStore { Configured = true };
        var handler = new FakePhotoHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        };
        var service = CreateService(store, handler);

        var photos = await service.RehostForIngestAsync(
            new[] { GoogleUrl }, "Failing Place", TestContext.Current.CancellationToken);

        Assert.Null(photos); // vacío → null: mejor sin foto que con la key expuesta
    }

    [Fact]
    public async Task RehostForIngest_ConfiguredAndExternalFails_KeepsOriginal()
    {
        // Un hotlink externo (sin key) que falla se conserva — el backfill lo reintenta.
        var store = new FakeR2ObjectStore { Configured = true };
        var handler = new FakePhotoHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        };
        var service = CreateService(store, handler);

        var photos = await service.RehostForIngestAsync(
            new[] { WanderlogUrl }, "Hotlink Place", TestContext.Current.CancellationToken);

        Assert.NotNull(photos);
        Assert.Equal(WanderlogUrl, Assert.Single(photos));
    }

    [Fact]
    public async Task RehostForIngest_NotConfigured_KeepsOriginals_EvenGoogle()
    {
        // Degradación graceful sin credenciales: se persiste la original (patrón Mapbox/Klaviyo).
        // El cliente queda protegido igualmente por el filtro key= de PlaceDto.
        var store = new FakeR2ObjectStore();
        var service = CreateService(store);

        var photos = await service.RehostForIngestAsync(
            new[] { GoogleUrl, WanderlogUrl }, "No Config", TestContext.Current.CancellationToken);

        Assert.Equal(new[] { GoogleUrl, WanderlogUrl }, photos);
    }

    [Fact]
    public async Task RehostForIngest_NullOrEmpty_ReturnsNull()
    {
        var service = CreateService(new FakeR2ObjectStore { Configured = true });

        Assert.Null(await service.RehostForIngestAsync(null, "X", TestContext.Current.CancellationToken));
        Assert.Null(await service.RehostForIngestAsync(
            Array.Empty<string>(), "X", TestContext.Current.CancellationToken));
    }

    // ── SSRF / hardening de la descarga (m2, m3, M4) ──────────────────────

    [Theory]
    [InlineData("http://places.googleapis.com/v1/p/media?key=S")]      // https estricto
    [InlineData("https://cdn.example.com/photo.jpg")]                  // host fuera de allowlist
    [InlineData("https://evil.com/places.googleapis.com/photo.jpg")]   // host real evil.com
    [InlineData("https://169.254.169.254/latest/meta-data")]           // IMDS
    [InlineData("https://10.0.0.8/internal.jpg")]                      // IP privada
    [InlineData("https://[::1]/photo.jpg")]                            // IPv6 loopback
    [InlineData("https://localhost/photo.jpg")]
    [InlineData("https://api.internal/photo.jpg")]
    [InlineData("ftp://wanderlog.com/photo.jpg")]
    [InlineData("not-a-url")]
    public async Task Rehost_DisallowedSource_IsBlockedWithoutAnyNetworkCall(string url)
    {
        var store = new FakeR2ObjectStore { Configured = true };
        var handler = new FakePhotoHandler();
        var service = CreateService(store, handler);

        var result = await service.RehostAsync(url, "Blocked", TestContext.Current.CancellationToken);

        Assert.Equal(PhotoRehostOutcome.Failed, result.Outcome);
        Assert.Equal(PhotoRehostFailureStage.Blocked, result.FailureStage);
        Assert.Equal(url, result.Url);
        Assert.Empty(handler.Calls); // bloqueado ANTES de tocar la red
        Assert.Empty(store.Objects);
    }

    [Theory]
    [InlineData("https://places.googleapis.com/v1/p/media?maxWidthPx=1600&key=S")]
    [InlineData("https://wanderlog.com/photos/a.jpg")]
    [InlineData("https://itin-dev.wanderlog.com/photos/a.jpg")]  // wildcard *.wanderlog.com
    [InlineData("https://lh3.googleusercontent.com/p/photo")]    // wildcard *.googleusercontent.com
    public async Task Rehost_AllowlistedSource_IsFetched(string url)
    {
        var store = new FakeR2ObjectStore { Configured = true };
        var handler = new FakePhotoHandler();
        var service = CreateService(store, handler);

        var result = await service.RehostAsync(url, "Allowed", TestContext.Current.CancellationToken);

        Assert.Equal(PhotoRehostOutcome.Rehosted, result.Outcome);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task Rehost_RedirectResponse_FailsInsteadOfFollowing()
    {
        // AllowAutoRedirect=false en el handler de producción; el servicio además trata
        // cualquier no-2xx como fallo — un 302 desde una fuente allowlisted no puede
        // arrastrar el GET a otro host.
        var store = new FakeR2ObjectStore { Configured = true };
        var redirect = new HttpResponseMessage(HttpStatusCode.Found);
        redirect.Headers.Location = new Uri("https://internal-target.example/steal");
        var handler = new FakePhotoHandler { Responder = _ => redirect };
        var service = CreateService(store, handler);

        var result = await service.RehostAsync(WanderlogUrl, "Redirected", TestContext.Current.CancellationToken);

        Assert.Equal(PhotoRehostOutcome.Failed, result.Outcome);
        Assert.Equal(PhotoRehostFailureStage.Download, result.FailureStage);
        Assert.Single(handler.Calls); // no siguió el Location
        Assert.Empty(store.Objects);
    }

    [Fact]
    public async Task Rehost_NonImageContentType_IsRejected()
    {
        var store = new FakeR2ObjectStore { Configured = true };
        var html = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>not a photo</html>", System.Text.Encoding.UTF8, "text/html")
        };
        var handler = new FakePhotoHandler { Responder = _ => html };
        var service = CreateService(store, handler);

        var result = await service.RehostAsync(WanderlogUrl, "Html", TestContext.Current.CancellationToken);

        Assert.Equal(PhotoRehostOutcome.Failed, result.Outcome);
        Assert.Equal(PhotoRehostFailureStage.Download, result.FailureStage);
        Assert.Empty(store.Objects);
    }

    [Fact]
    public async Task Rehost_ContentLengthOverLimit_IsRejected()
    {
        var store = new FakeR2ObjectStore { Configured = true };
        var big = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(FakePhotoHandler.CreateJpeg(100, 100))
        };
        big.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        big.Content.Headers.ContentLength = PhotoRehostService.MaxDownloadBytes + 1;
        var handler = new FakePhotoHandler { Responder = _ => big };
        var service = CreateService(store, handler);

        var result = await service.RehostAsync(WanderlogUrl, "TooBig", TestContext.Current.CancellationToken);

        Assert.Equal(PhotoRehostOutcome.Failed, result.Outcome);
        Assert.Equal(PhotoRehostFailureStage.Download, result.FailureStage);
        Assert.Empty(store.Objects);
    }

    [Fact]
    public async Task Rehost_OversizedDimensions_AreRejectedBeforeFullDecode()
    {
        // Decompression bomb: las dimensiones se validan con Identify (solo header)
        // antes de materializar el bitmap con Image.Load.
        var store = new FakeR2ObjectStore { Configured = true };
        var handler = new FakePhotoHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(
                    FakePhotoHandler.CreateJpeg(PhotoRehostService.MaxSourceDimensionPx + 8, 8))
            }
        };
        var service = CreateService(store, handler);

        var result = await service.RehostAsync(WanderlogUrl, "Bomb", TestContext.Current.CancellationToken);

        Assert.Equal(PhotoRehostOutcome.Failed, result.Outcome);
        Assert.Equal(PhotoRehostFailureStage.Decode, result.FailureStage);
        Assert.Empty(store.Objects);
    }

    [Fact]
    public async Task Rehost_UploadTimeout_DegradesToFailed_NeverPropagatesCancellation()
    {
        // M4: el timeout del cliente S3 aflora como TaskCanceledException SIN que el ct del
        // caller esté cancelado. Debe degradar a Failed(Upload) — nunca abortar la request
        // que creaba el place.
        var store = new FakeR2ObjectStore
        {
            Configured = true,
            UploadFailure = _ => new TaskCanceledException("simulated S3 timeout"),
        };
        var service = CreateService(store);

        var result = await service.RehostAsync(WanderlogUrl, "Hung R2", TestContext.Current.CancellationToken);

        Assert.Equal(PhotoRehostOutcome.Failed, result.Outcome);
        Assert.Equal(PhotoRehostFailureStage.Upload, result.FailureStage);
        Assert.Equal(WanderlogUrl, result.Url);
    }

    [Fact]
    public async Task Rehost_CallerCancellation_DoesPropagate()
    {
        // La cancelación REAL del caller (proxy/cliente abortó) sí debe propagar — solo los
        // timeouts internos degradan.
        var store = new FakeR2ObjectStore { Configured = true };
        using var cts = new CancellationTokenSource();
        var handler = new FakePhotoHandler
        {
            AsyncResponder = (_, ct) =>
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(FakePhotoHandler.DefaultOk());
            }
        };
        var service = CreateService(store, handler);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.RehostAsync(WanderlogUrl, "Canceled", cts.Token));
    }

    [Fact]
    public void BuildObjectKey_IgnoresQueryString_SoKeyRotationDoesNotChangeTheKey()
    {
        var a = PhotoRehostService.BuildObjectKey("Broken Shaker",
            "https://places.googleapis.com/v1/places/abc/photos/def/media?maxWidthPx=1600&key=OLD");
        var b = PhotoRehostService.BuildObjectKey("Broken Shaker",
            "https://places.googleapis.com/v1/places/abc/photos/def/media?maxWidthPx=800&key=NEW");
        var other = PhotoRehostService.BuildObjectKey("Broken Shaker",
            "https://places.googleapis.com/v1/places/abc/photos/OTHER/media?key=OLD");

        Assert.Equal(a, b);          // misma foto → mismo objeto (idempotente)
        Assert.NotEqual(a, other);   // foto distinta → objeto distinto
        Assert.Matches(@"^places/broken-shaker-[0-9a-f]{8}\.webp$", a);
    }

    [Fact]
    public void Slugify_MatchesJsPipelineBehaviour()
    {
        Assert.Equal("joes-stone-crab", PhotoRehostService.Slugify("Joe's Stone Crab"));
        Assert.Equal("ball-and-chain", PhotoRehostService.Slugify("Ball & Chain"));
        // Igual que el script JS: los no-ASCII colapsan en el separador.
        Assert.Equal("caf-la-trova", PhotoRehostService.Slugify("  Café La Trova!  "));
        // Nombre sin caracteres slug-eables → fallback "photo" en el object key.
        Assert.StartsWith("places/photo-", PhotoRehostService.BuildObjectKey("???", "https://example.com/a.jpg"));
    }
}

public class PhotoUrlsTests
{
    [Theory]
    [InlineData("https://places.googleapis.com/v1/p/media?maxWidthPx=1600&key=SECRET", true)]
    [InlineData("https://example.com/img.jpg?key=abc", true)]
    [InlineData("https://example.com/img.jpg?KEY=abc", true)]
    [InlineData("https://example.com/img.jpg?monkey=abc", false)]
    [InlineData("https://example.com/img.jpg?donkey=1&key=2", true)]
    [InlineData("https://example.com/key=notaquery/img.jpg", false)]
    [InlineData("https://pub-x.r2.dev/places/foo.webp", false)]
    public void ContainsApiKey_DetectsKeyQueryParamOnly(string url, bool expected) =>
        Assert.Equal(expected, PhotoUrls.ContainsApiKey(url));

    [Fact]
    public void Sanitize_FiltersOnlyKeyUrls_AndReturnsSameInstanceWhenClean()
    {
        var clean = new List<string> { "https://pub-x.r2.dev/places/a.webp" };
        Assert.Same(clean, PhotoUrls.Sanitize(clean));

        var mixed = new List<string>
        {
            "https://pub-x.r2.dev/places/a.webp",
            "https://places.googleapis.com/v1/p/media?key=SECRET",
        };
        var sanitized = PhotoUrls.Sanitize(mixed);
        Assert.NotNull(sanitized);
        Assert.Equal("https://pub-x.r2.dev/places/a.webp", Assert.Single(sanitized));

        Assert.Null(PhotoUrls.Sanitize(null));
    }

    [Theory]
    [InlineData("https://places.googleapis.com/v1/p/media?key=S", null, PhotoUrls.BucketGoogle)]
    [InlineData("https://pub-abc.r2.dev/places/a.webp", null, PhotoUrls.BucketR2)]
    [InlineData("https://images.locallist.ai/places/a.webp", "images.locallist.ai", PhotoUrls.BucketR2)]
    [InlineData("https://wanderlog.com/photo.jpg", null, PhotoUrls.BucketWanderlog)]
    [InlineData("https://cdn.wanderlog.com/photo.jpg", null, PhotoUrls.BucketWanderlog)]
    [InlineData("https://images.locallist.ai/places/a.webp", null, PhotoUrls.BucketOther)]
    [InlineData("https://example.com/a.jpg", null, PhotoUrls.BucketOther)]
    [InlineData("not-a-url", null, PhotoUrls.BucketOther)]
    public void DomainBucket_ClassifiesHosts(string url, string? r2Host, string expected) =>
        Assert.Equal(expected, PhotoUrls.DomainBucket(url, r2Host));
}
