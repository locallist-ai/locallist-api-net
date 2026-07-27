using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using LocalList.API.NET.Features.Import;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.Tests.Features;

/// <summary>
/// F2 T1 — <c>POST /import/video</c> sobre DB real (ApiFixture = Testcontainers PostgreSQL) y el
/// <see cref="FakeGeminiFileApi"/> del harness de T2 (File API + generateContent simulados). Cubre:
/// gate Plus, auth, validaciones baratas sin gasto de cuota, doble cuota (30/mes·10/día) con carrera
/// concurrente exacta, gating de terceros, reembolso de cuota en fallo de extracción, y la capability
/// de /account. La DB nunca se mockea.
/// </summary>
public class ImportEndpointTests(ApiFixture fixture) : IClassFixture<ApiFixture>, IDisposable
{
    public void Dispose() => fixture.FakeVideoImport.Reset();

    // ── helpers ────────────────────────────────────────────────────────────────

    private async Task<(HttpClient client, Guid userId)> PlusClient(string tag)
    {
        var uid = Guid.NewGuid();
        var client = await fixture.CreateAppAuthenticatedClientWithUser(uid, $"{tag}-{uid:N}@test.com", tier: "pro");
        return (client, uid);
    }

    /// <summary>Multipart SOLO con el fichero: los metadatos van en la query string (contrato T1).</summary>
    private static MultipartFormDataContent VideoForm(
        string mime = "video/mp4", int bytes = 2048, string fileName = "clip.mp4", bool includeFile = true)
    {
        var form = new MultipartFormDataContent();
        if (includeFile)
        {
            var file = new ByteArrayContent(new byte[bytes]);
            file.Headers.ContentType = new MediaTypeHeaderValue(mime);
            form.Add(file, "video", fileName);
        }
        return form;
    }

    /// <summary>URL del endpoint con los metadatos en query string (platform/creatorHandle).</summary>
    private static string Url(string? platform = null, string? creatorHandle = null)
    {
        var qs = new List<string>();
        if (platform is not null) qs.Add($"platform={Uri.EscapeDataString(platform)}");
        if (creatorHandle is not null) qs.Add($"creatorHandle={Uri.EscapeDataString(creatorHandle)}");
        return "/import/video" + (qs.Count > 0 ? "?" + string.Join("&", qs) : string.Empty);
    }

    private async Task<int> Count(Guid userId, string feature, DateOnly period)
    {
        var db = fixture.GetDbContext();
        return await db.UsageCounters
            .Where(uc => uc.UserId == userId && uc.Feature == feature && uc.PeriodStart == period)
            .Select(uc => uc.Count).FirstOrDefaultAsync();
    }

    private DateOnly Today() => DateOnly.FromDateTime(fixture.FakeTime.GetUtcNow().UtcDateTime);
    private DateOnly MonthStart()
    {
        var n = fixture.FakeTime.GetUtcNow();
        return new DateOnly(n.Year, n.Month, 1);
    }

    private async Task<int> Daily(Guid u) => await Count(u, ImportController.FeatureDaily, Today());
    private async Task<int> Monthly(Guid u) => await Count(u, ImportController.FeatureMonthly, MonthStart());

    // ── (a) happy path Plus ──────────────────────────────────────────────────────
    [Fact]
    public async Task Plus_ValidUpload_Returns200_PersistsMetric_ConsumesQuota()
    {
        var (client, userId) = await PlusClient("imp-happy");

        // Ciudad ÚNICA de ESTE request: el metric no guarda identidad del uploader (retención),
        // así que la clave de scoping para recuperar LA fila de este test es la city extraída —
        // filtrar por Platform+CreatedAt agarraba el metric de otro test bajo paralelismo/empate
        // de timestamps (flaky 1/1200).
        var city = "ImpHappyCity" + Guid.NewGuid().ToString("N")[..10];
        fixture.FakeVideoImport.GenerateContentResponder = _ =>
            fixture.FakeVideoImport.GenerateContentOk($$"""
                {
                  "city": "{{city}}",
                  "country": "USA",
                  "language": "en",
                  "places": [
                    { "name": "Sunny Rooftop", "descriptor": "rooftop bar en Wynwood", "category": "nightlife", "evidence": "ocr", "timestampSec": 12 }
                  ],
                  "vibes": ["sunset", "cocktails"],
                  "confidence": 0.82
                }
                """);

        var res = await client.PostAsync(Url("self", "@chef"), VideoForm());
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(city, body.GetProperty("city").GetString());
        Assert.Equal("self", body.GetProperty("platform").GetString());
        // Handle saneado con la semántica ESTRICTA unificada (T4): se guarda/echoa SIN el '@'.
        Assert.Equal("chef", body.GetProperty("creatorHandle").GetString());
        var places = body.GetProperty("places");
        Assert.Equal(1, places.GetArrayLength());
        Assert.Equal("Sunny Rooftop", places[0].GetProperty("name").GetString());
        Assert.Equal("Nightlife", places[0].GetProperty("category").GetString());
        // No filtra internals de Gemini.
        Assert.False(body.TryGetProperty("diagnostics", out _));
        Assert.False(body.TryGetProperty("fileUri", out _));

        // Métrica persistida — scoped por la city única de ESTE request (SingleAsync: si esta
        // query pudiera devolver >1 fila, el test debe fallar ruidosamente, no elegir una).
        var db = fixture.GetDbContext();
        var metric = await db.VideoImportMetrics.SingleAsync(m => m.City == city);
        Assert.Null(metric.ErrorCode);
        Assert.Equal(1, metric.NumPlaces);
        Assert.Equal("self", metric.Platform);

        // Cuota consumida en ambas ventanas; fichero remoto borrado (no retención).
        Assert.Equal(1, await Daily(userId));
        Assert.Equal(1, await Monthly(userId));
        Assert.Contains("files/test-video-abc", fixture.FakeVideoImport.DeleteCalledFor);
    }

    // ── (a2) sin platform en la query → default self (happy path, NO 403 terceros) ──
    [Fact]
    public async Task WithoutPlatform_DefaultsToSelf()
    {
        var (client, _) = await PlusClient("imp-noplat");
        var city = "ImpNoPlatCity" + Guid.NewGuid().ToString("N")[..10];
        fixture.FakeVideoImport.GenerateContentResponder = _ =>
            fixture.FakeVideoImport.GenerateContentOk($$"""
                {
                  "city": "{{city}}",
                  "country": "USA",
                  "language": "en",
                  "places": [
                    { "name": "Corner Cafe", "descriptor": "cafe", "category": "coffee", "evidence": "ocr", "timestampSec": 3 }
                  ],
                  "vibes": [],
                  "confidence": 0.7
                }
                """);

        // Sin ?platform= en la query: el default debe ser "self" → pasa el gate de terceros
        // (flag OFF) y llega a la extracción, NO un 403 third_party_import_disabled.
        var res = await client.PostAsync(Url(), VideoForm());
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("self", body.GetProperty("platform").GetString());
        Assert.True(fixture.FakeVideoImport.GenerateContentCalled);
    }

    // ── (b) free → 403 import_requires_plus, sin cuota ni Gemini ─────────────────
    [Fact]
    public async Task FreeUser_Returns403RequiresPlus_NoQuota_NoGemini()
    {
        var uid = Guid.NewGuid();
        var client = await fixture.CreateAppAuthenticatedClientWithUser(uid, $"imp-free-{uid:N}@test.com", tier: "free");

        var res = await client.PostAsync(Url("self"), VideoForm());
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("import_requires_plus", body.GetProperty("error").GetString());

        Assert.Equal(0, await Daily(uid));
        Assert.Equal(0, await Monthly(uid));
        Assert.False(fixture.FakeVideoImport.UploadStarted);
        Assert.False(fixture.FakeVideoImport.GenerateContentCalled);
    }

    // ── (c) anónimo → 401 ────────────────────────────────────────────────────────
    [Fact]
    public async Task Anonymous_Returns401()
    {
        var client = fixture.CreateClient();
        var res = await client.PostAsync(Url(), VideoForm());
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.False(fixture.FakeVideoImport.UploadStarted);
    }

    // ── (d) mime inválido → 400 sin consumir cuota ───────────────────────────────
    [Fact]
    public async Task InvalidMime_Returns400_NoQuota_NoUpload()
    {
        var (client, userId) = await PlusClient("imp-mime");

        var res = await client.PostAsync(Url("self"),
            VideoForm(mime: "image/gif", fileName: "x.gif"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("import_unsupported_format", body.GetProperty("error").GetString());

        Assert.Equal(0, await Daily(userId));
        Assert.Equal(0, await Monthly(userId));
        Assert.False(fixture.FakeVideoImport.UploadStarted);
    }

    // ── (d) multipart sin part de fichero → 400 import_missing_file ───────────────
    [Fact]
    public async Task MissingFile_Returns400()
    {
        var (client, _) = await PlusClient("imp-nofile");
        // Multipart VÁLIDO pero sin fichero (solo un campo de texto, que el endpoint ignora).
        var form = VideoForm(includeFile: false);
        form.Add(new StringContent("ignored"), "note");
        var res = await client.PostAsync(Url("self"), form);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("import_missing_file", body.GetProperty("error").GetString());
    }

    // ── (d') multipart vacío/malformado → 400 import_invalid_request (nunca 500) ──
    [Fact]
    public async Task EmptyMultipart_Returns400InvalidRequest()
    {
        var (client, _) = await PlusClient("imp-empty");
        var res = await client.PostAsync(Url("self"), VideoForm(includeFile: false)); // 0 parts
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("import_invalid_request", body.GetProperty("error").GetString());
    }

    // ── (e) cuota diaria 10/10 → 429 import_limit_reached (window daily) ──────────
    [Fact]
    public async Task DailyQuotaExhausted_Returns429_WindowDaily()
    {
        var (client, userId) = await PlusClient("imp-daily");
        var db = fixture.GetDbContext();
        db.UsageCounters.Add(new UsageCounter
        {
            UserId = userId, Feature = ImportController.FeatureDaily,
            PeriodStart = Today(), Count = ImportController.DailyLimit,
        });
        await db.SaveChangesAsync();

        var res = await client.PostAsync(Url("self"), VideoForm());
        Assert.Equal(HttpStatusCode.TooManyRequests, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("import_limit_reached", body.GetProperty("error").GetString());
        Assert.Equal("daily", body.GetProperty("window").GetString());
        Assert.Equal(ImportController.DailyLimit, body.GetProperty("limit").GetInt32());

        // No arrancó la extracción; el diario quedó exactamente en el tope (no se movió).
        Assert.False(fixture.FakeVideoImport.UploadStarted);
        Assert.Equal(ImportController.DailyLimit, await Daily(userId));
    }

    // ── (e) cuota mensual al tope → 429 (window monthly) + REEMBOLSO del diario ───
    [Fact]
    public async Task MonthlyQuotaExhausted_Returns429_WindowMonthly_RefundsDailySlot()
    {
        var (client, userId) = await PlusClient("imp-monthly");
        var db = fixture.GetDbContext();
        db.UsageCounters.Add(new UsageCounter
        {
            UserId = userId, Feature = ImportController.FeatureMonthly,
            PeriodStart = MonthStart(), Count = ImportController.MonthlyLimit,
        });
        await db.SaveChangesAsync();

        var res = await client.PostAsync(Url("self"), VideoForm());
        Assert.Equal(HttpStatusCode.TooManyRequests, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("import_limit_reached", body.GetProperty("error").GetString());
        Assert.Equal("monthly", body.GetProperty("window").GetString());

        // El diario se consumió (0→1) y se DEVOLVIÓ al fallar el mensual: queda en 0.
        Assert.Equal(0, await Daily(userId));
        Assert.Equal(ImportController.MonthlyLimit, await Monthly(userId));
        Assert.False(fixture.FakeVideoImport.UploadStarted);
    }

    // ── (e) carrera concurrente en el límite diario → exactamente DailyLimit pasan ─
    [Fact]
    public async Task ConcurrentAtDailyLimit_ExactlyDailyLimitSucceed()
    {
        var (client, userId) = await PlusClient("imp-conc");
        const int fired = ImportController.DailyLimit + 3;

        var responses = await Task.WhenAll(Enumerable.Range(0, fired).Select(_ =>
            client.PostAsync(Url("self"), VideoForm())));

        var ok = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        var limited = responses.Count(r => r.StatusCode == HttpStatusCode.TooManyRequests);
        Assert.Equal(ImportController.DailyLimit, ok);
        Assert.Equal(3, limited);

        // El contador diario quedó EXACTAMENTE en el tope: ningún interleaving coló un extra.
        Assert.Equal(ImportController.DailyLimit, await Daily(userId));
    }

    // ── (f) platform=tiktok con flag OFF → 403 third_party_import_disabled ────────
    [Fact]
    public async Task ThirdPartyPlatform_FlagOff_Returns403Disabled_NoQuota()
    {
        var (client, userId) = await PlusClient("imp-3p");

        var res = await client.PostAsync(Url("tiktok"), VideoForm());
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("third_party_import_disabled", body.GetProperty("error").GetString());

        Assert.Equal(0, await Daily(userId));
        Assert.Equal(0, await Monthly(userId));
        // platform viaja en la query → el gate corre PRE-BODY: no se streameó ni subió nada.
        Assert.False(fixture.FakeVideoImport.UploadStarted);
        Assert.False(fixture.FakeVideoImport.GenerateContentCalled);
    }

    // ── (g) ExtractionUnavailable SIN facturar (HTTP no-2xx de generate) → 503 + REEMBOLSO ──
    [Fact]
    public async Task ExtractionUnavailable_Returns503_RefundsBothWindows()
    {
        var (client, userId) = await PlusClient("imp-unavail");
        fixture.FakeVideoImport.GenerateContentResponder = _ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("{\"error\":\"boom\"}", System.Text.Encoding.UTF8, "application/json")
            };

        var res = await client.PostAsync(Url("self"), VideoForm());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("import_unavailable", body.GetProperty("error").GetString());

        // Consumió ambas ventanas y las devolvió al no entregar valor: quedan en 0.
        Assert.Equal(0, await Daily(userId));
        Assert.Equal(0, await Monthly(userId));
        // El fichero remoto se borró igualmente (invariante de no retención de T2).
        Assert.Contains("files/test-video-abc", fixture.FakeVideoImport.DeleteCalledFor);
    }

    // ── (g') NoPlacesFound → 422 y la cuota SÍ se mantiene (Gemini pagó) ──────────
    [Fact]
    public async Task NoPlacesFound_Returns422_QuotaStaysConsumed()
    {
        var (client, userId) = await PlusClient("imp-noplaces");
        fixture.FakeVideoImport.GenerateContentResponder = _ =>
            fixture.FakeVideoImport.GenerateContentOk(
                """{ "city":"Miami","country":"USA","language":"en","places":[],"vibes":[],"confidence":0.1 }""");

        var res = await client.PostAsync(Url("self"), VideoForm());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("no_places_found", body.GetProperty("error").GetString());

        Assert.Equal(1, await Daily(userId));
        Assert.Equal(1, await Monthly(userId));
    }

    // ── (g'') Fallos POST-2xx de generateContent (Billed): 503 pero la cuota SE MANTIENE ──
    // Los tres los provoca el CONTENIDO del vídeo (repro del reviewer): si se reembolsaran,
    // un atacante encadenaría llamadas multimodales caras (~150k tokens de input c/u, YA
    // facturadas por Gemini) con cuota siempre a 0, solo acotado por el techo por IP.

    [Fact]
    public async Task GenerateTruncatedMaxTokens_Returns503_QuotaStaysConsumed()
    {
        var (client, userId) = await PlusClient("imp-maxtok");
        // 2xx con finishReason=MAX_TOKENS → ExtractionUnavailable("truncated", billed:true).
        fixture.FakeVideoImport.GenerateContentResponder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                { "candidates": [ { "content": { "parts": [ { "text": "{\"pl" } ] }, "finishReason": "MAX_TOKENS" } ],
                  "usageMetadata": { "promptTokenCount": 150000, "candidatesTokenCount": 4096 } }
                """, System.Text.Encoding.UTF8, "application/json"),
        };

        var res = await client.PostAsync(Url("self"), VideoForm());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("import_unavailable", body.GetProperty("error").GetString());

        // Gemini facturó la llamada (2xx) → SIN reembolso: ambas ventanas quedan consumidas.
        Assert.True(fixture.FakeVideoImport.GenerateContentCalled);
        Assert.Equal(1, await Daily(userId));
        Assert.Equal(1, await Monthly(userId));
    }

    [Fact]
    public async Task GenerateContentFilteredSafety_Returns503_QuotaStaysConsumed()
    {
        var (client, userId) = await PlusClient("imp-safety");
        // 2xx sin parts y finishReason=SAFETY → ExtractionUnavailable("content_filtered_SAFETY", billed:true).
        fixture.FakeVideoImport.GenerateContentResponder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                { "candidates": [ { "finishReason": "SAFETY" } ],
                  "usageMetadata": { "promptTokenCount": 150000, "candidatesTokenCount": 0 } }
                """, System.Text.Encoding.UTF8, "application/json"),
        };

        var res = await client.PostAsync(Url("self"), VideoForm());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);

        Assert.True(fixture.FakeVideoImport.GenerateContentCalled);
        Assert.Equal(1, await Daily(userId));
        Assert.Equal(1, await Monthly(userId));
    }

    [Fact]
    public async Task GenerateInvalidJson_Returns503_QuotaStaysConsumed()
    {
        var (client, userId) = await PlusClient("imp-badjson");
        // 2xx con texto no parseable → ExtractionUnavailable("invalid_json", billed:true).
        fixture.FakeVideoImport.GenerateContentResponder = _ =>
            fixture.FakeVideoImport.GenerateContentOk("{{{ not json at all");

        var res = await client.PostAsync(Url("self"), VideoForm());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);

        Assert.True(fixture.FakeVideoImport.GenerateContentCalled);
        Assert.Equal(1, await Daily(userId));
        Assert.Equal(1, await Monthly(userId));
    }

    // ── (g''') Regresión: fallo PRE-facturación (duration_unknown) → sí reembolsa ──
    [Fact]
    public async Task DurationUnknown_FailsBeforeGenerate_Returns503_RefundsBothWindows()
    {
        var (client, userId) = await PlusClient("imp-durunk");
        // ACTIVE sin videoDuration → fail-closed ANTES de generateContent (nada facturado).
        fixture.FakeVideoImport.OmitDurationOnActive = true;

        var res = await client.PostAsync(Url("self"), VideoForm());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);

        Assert.False(fixture.FakeVideoImport.GenerateContentCalled);
        Assert.Equal(0, await Daily(userId));
        Assert.Equal(0, await Monthly(userId));
    }

    // ── (h) /account expone la capability (default false) ────────────────────────
    [Fact]
    public async Task Account_ExposesImportThirdPartyCapability_DefaultFalse()
    {
        var uid = Guid.NewGuid();
        var client = await fixture.CreateAppAuthenticatedClientWithUser(uid, $"imp-acct-{uid:N}@test.com", tier: "pro");

        var res = await client.GetAsync("/account");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("importThirdPartyEnabled", out var flag));
        Assert.False(flag.GetBoolean());
    }

    // ── (img) imagen (image/jpeg) aceptada → 200, sin check de duración, cuota consumida ──
    [Fact]
    public async Task ImageUpload_Accepted_Returns200_ConsumesQuota()
    {
        var (client, userId) = await PlusClient("imp-img");
        // Imagen real: la "verdad" autoritativa del File API es un mime image/* SIN videoDuration →
        // el camino de imagen NO lo trata como fallo (a diferencia del vídeo, que daría
        // duration_unknown) NI lo confunde con un vídeo disfrazado (media_type_mismatch).
        fixture.FakeVideoImport.ActiveMimeType = "image/jpeg";
        fixture.FakeVideoImport.OmitDurationOnActive = true;
        var city = "ImpImgCity" + Guid.NewGuid().ToString("N")[..10];
        fixture.FakeVideoImport.GenerateContentResponder = _ =>
            fixture.FakeVideoImport.GenerateContentOk($$"""
                {
                  "city": "{{city}}",
                  "country": "USA",
                  "language": "en",
                  "places": [
                    { "name": "Corner Cafe", "descriptor": "cafe en la lista", "category": "coffee", "evidence": "ocr", "timestampSec": 0 }
                  ],
                  "vibes": ["cozy"],
                  "confidence": 0.6
                }
                """);

        var res = await client.PostAsync(Url("self"), VideoForm(mime: "image/jpeg", fileName: "list.jpg"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(city, body.GetProperty("city").GetString());
        Assert.Equal(1, body.GetProperty("places").GetArrayLength());
        Assert.True(fixture.FakeVideoImport.GenerateContentCalled);

        var db = fixture.GetDbContext();
        var metric = await db.VideoImportMetrics.SingleAsync(m => m.City == city);
        Assert.Null(metric.ErrorCode);
        Assert.Equal("image/jpeg", metric.MimeType);
        Assert.Null(metric.DurationSec);

        Assert.Equal(1, await Daily(userId));
        Assert.Equal(1, await Monthly(userId));
    }

    // ── (img') vídeo DISFRAZADO de imagen → 400 import_media_type_mismatch + REEMBOLSO ──
    // Sube declarando image/jpeg, pero la metadata autoritativa del File API es un vídeo (video/mp4
    // + duración). El camino imagen se salta el cap legal de duración, así que un vídeo real se
    // colaría sin control — el servicio lo detecta post-metadata y rechaza ANTES de facturar
    // generateContent: 4xx claro y cuota reembolsada (fallo pre-facturación), sin gasto de Gemini.
    [Fact]
    public async Task ImageDeclared_ButAuthoritativeVideo_Returns400Mismatch_RefundsBothWindows()
    {
        var (client, userId) = await PlusClient("imp-spoof");
        // La subida declara image/jpeg; el File API reporta el tipo REAL: video/mp4 de 700s.
        fixture.FakeVideoImport.ActiveMimeType = "video/mp4";
        fixture.FakeVideoImport.DurationSec = 700;

        var res = await client.PostAsync(Url("self"), VideoForm(mime: "image/jpeg", fileName: "list.jpg"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("import_media_type_mismatch", body.GetProperty("error").GetString());

        // NO se facturó generateContent (rechazo pre-generate) → ambas ventanas reembolsadas a 0.
        Assert.False(fixture.FakeVideoImport.GenerateContentCalled);
        Assert.Equal(0, await Daily(userId));
        Assert.Equal(0, await Monthly(userId));
        // El fichero remoto se borró igualmente (invariante de no retención de T2).
        Assert.Contains("files/test-video-abc", fixture.FakeVideoImport.DeleteCalledFor);
    }

    // ── (c) MIME no permitido (application/pdf) → 400 import_unsupported_format, sin cuota ──
    [Fact]
    public async Task UnsupportedFormat_Pdf_Returns400_NoQuota_NoUpload()
    {
        var (client, userId) = await PlusClient("imp-pdf");

        var res = await client.PostAsync(Url("self"),
            VideoForm(mime: "application/pdf", fileName: "itinerary.pdf"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("import_unsupported_format", body.GetProperty("error").GetString());

        Assert.Equal(0, await Daily(userId));
        Assert.Equal(0, await Monthly(userId));
        Assert.False(fixture.FakeVideoImport.UploadStarted);
    }
}

/// <summary>
/// Fixture con <c>Import:ThirdPartyEnabled=true</c> — verifica que el gating de terceros se
/// abre por config y que /account refleja la capability. Container propio (config distinta).
/// </summary>
public sealed class ImportThirdPartyEnabledFixture : ApiFixture
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Import:ThirdPartyEnabled", "true");
        base.ConfigureWebHost(builder);
    }
}

public class ImportThirdPartyEnabledTests(ImportThirdPartyEnabledFixture fixture)
    : IClassFixture<ImportThirdPartyEnabledFixture>, IDisposable
{
    public void Dispose() => fixture.FakeVideoImport.Reset();

    private static MultipartFormDataContent VideoForm()
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(new byte[2048]);
        file.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        form.Add(file, "video", "clip.mp4");
        return form;
    }

    // ── (f) platform=tiktok con flag ON → pasa al servicio (200) ─────────────────
    [Fact]
    public async Task ThirdPartyPlatform_FlagOn_ReachesService_Returns200()
    {
        var uid = Guid.NewGuid();
        var client = await fixture.CreateAppAuthenticatedClientWithUser(uid, $"imp3p-on-{uid:N}@test.com", tier: "pro");

        var res = await client.PostAsync("/import/video?platform=tiktok", VideoForm());
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("tiktok", body.GetProperty("platform").GetString());
        Assert.True(fixture.FakeVideoImport.GenerateContentCalled);

        var acct = await (await client.GetAsync("/account")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(acct.GetProperty("importThirdPartyEnabled").GetBoolean());
    }
}

/// <summary>
/// Fixture con <c>Import:MaxSizeBytes</c> diminuto — verifica el rechazo por tamaño en el
/// STREAMING (cap durante la copia al temp file), antes de consumir cuota. Container propio.
/// </summary>
public sealed class ImportSmallSizeFixture : ApiFixture
{
    public const long SmallMax = 512;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Import:MaxSizeBytes", SmallMax.ToString());
        base.ConfigureWebHost(builder);
    }
}

public class ImportSizeLimitTests(ImportSmallSizeFixture fixture)
    : IClassFixture<ImportSmallSizeFixture>, IDisposable
{
    public void Dispose() => fixture.FakeVideoImport.Reset();

    // ── (d) size excedido → 400 import_too_large sin consumir cuota ──────────────
    [Fact]
    public async Task OversizedFile_Returns400TooLarge_NoQuota_NoUpload()
    {
        var uid = Guid.NewGuid();
        var client = await fixture.CreateAppAuthenticatedClientWithUser(uid, $"imp-size-{uid:N}@test.com", tier: "pro");

        var form = new MultipartFormDataContent();
        // 4 KB > 512 B → el cap del streaming lo corta antes de subir nada.
        var file = new ByteArrayContent(new byte[4096]);
        file.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
        form.Add(file, "video", "big.mp4");

        var res = await client.PostAsync("/import/video?platform=self", form);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("import_too_large", body.GetProperty("error").GetString());

        var db = fixture.GetDbContext();
        var daily = await db.UsageCounters
            .Where(uc => uc.UserId == uid && uc.Feature == ImportController.FeatureDaily)
            .Select(uc => uc.Count).FirstOrDefaultAsync();
        Assert.Equal(0, daily);
        Assert.False(fixture.FakeVideoImport.UploadStarted);
    }
}
