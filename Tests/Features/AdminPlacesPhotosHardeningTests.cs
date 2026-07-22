using System.Net.Http.Headers;
using System.Text.Json;
using LocalList.API.NET.Features.Admin.Places;
using LocalList.API.NET.Shared.Data;
using LocalList.API.NET.Shared.Data.Entities;
using LocalList.API.NET.Shared.Photos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LocalList.API.Tests.Features;

/// <summary>
/// Repros permanentes de los pases adversariales sobre fotos→R2 (2026-07-22), en verde
/// tras los fixes — si alguno vuelve a rojo, la regresión es exactamente la del review:
///
/// C1  backfill concurrente (lock + 409, descarga única) ·
/// M1  livelock/head-of-line del backfill (deferral con backoff → convergencia) ·
/// M2  lost update backfill↔PATCH (token xmin: merge en backfill, 409 en PATCH stale) ·
/// M3  circuit breaker de uploads a R2 (no quemar dinero de Google sin progreso) ·
/// M4  R2 colgado no tumba la creación de places ·
/// M5  import masivo: abort del caller a mitad NO revienta ni pierde nada (ronda 4: degradación
///     incondicional + commit que sobrevive el abort → se persisten TODOS los places) ·
/// M6  recuperación de fotos vía GooglePlaceId ·
/// m1  la key de Google no sale por la API admin ·
/// m2  SSRF: host fuera de allowlist ni siquiera se descarga ·
/// m3  conflicto xmin en RECOVERY → backoff (RecordFailure), no re-factura Google Details (ronda 4) ·
/// m4  places deleted/rejected fuera de candidatos y censo ·
/// G1  convergencia HONESTA: un fallo transitorio EN EL RUN → converged=false (ronda 2), y un
///     fallo de UPLOAD sub-umbral → unmigratedPlaces → converged=false (ronda 3) ·
/// G2  circuit breaker de INGESTA: R2 colgado → places sin foto, 2xx, sin excepción (ronda 2);
///     presupuesto de wall-clock que corta por TIEMPO, no solo por intentos (ronda 3) ·
/// R4  invariante (d) INCONDICIONAL: el abort del CALLER (proxy) durante R2 colgado degrada a "sin
///     foto" y el chunk se persiste — el corte no depende del budget (ronda 4) ·
/// g3  writers de Place stale → 409 concurrent_update, no 500 (ronda 2).
///
/// ApiFixture = Postgres real (Testcontainers); solo R2 y HTTP salientes fakeados.
/// </summary>
public class AdminPlacesPhotosHardeningTests(ApiFixture fixture) : IClassFixture<ApiFixture>, IDisposable
{
    private const string R2PublicUrl = "https://pub-7f09e69b5b644703825b6068a05dee8f.r2.dev";

    private static string GoogleUrl(string photoId) =>
        $"https://places.googleapis.com/v1/places/x/photos/{photoId}/media?maxWidthPx=1600&key=TEST-SECRET-KEY";

    private static string WanderlogUrl(string photoId) =>
        $"https://wanderlog.com/photos/{photoId}.jpg";

    public void Dispose()
    {
        fixture.FakeR2.Reset();
        fixture.FakePhotos.Reset();
        fixture.FakeGooglePlaces.Reset();
        fixture.Services.GetRequiredService<BackfillPhotosCoordinator>().Reset();
        // m4 (higiene): el presupuesto de wall-clock de la ingesta es un singleton IOptions
        // compartido por el host de test. Los tests que lo mutan lo aíslan con
        // `using WithIngestRehostBudget(...)` (restaura el valor previo al salir del scope). Este
        // restore al default es sólo una red de seguridad final por si algo lo dejara tocado.
        fixture.Services.GetRequiredService<IOptions<R2Options>>().Value.IngestRehostBudget =
            IngestPhotoBreaker.DefaultBudget;
    }

    /// <summary>
    /// Ajusta el presupuesto de wall-clock del rehost inline en ingesta (G2/ronda 3), acotado a
    /// un scope. El controller/import service leen <c>R2Options.IngestRehostBudget</c> por request
    /// al construir el <see cref="IngestPhotoBreaker"/>; mutar el singleton IOptions surte efecto.
    /// m4: captura el valor previo y lo restaura al hacer Dispose del <c>using</c>, así la mutación
    /// del singleton no filtra más allá del scope (robusto aunque se paralelizara, no sólo por el
    /// restore de Dispose del test).
    /// </summary>
    private IDisposable WithIngestRehostBudget(TimeSpan budget)
    {
        var options = fixture.Services.GetRequiredService<IOptions<R2Options>>().Value;
        var previous = options.IngestRehostBudget;
        options.IngestRehostBudget = budget;
        return new BudgetRestorer(options, previous);
    }

    private sealed class BudgetRestorer(R2Options options, TimeSpan previous) : IDisposable
    {
        public void Dispose() => options.IngestRehostBudget = previous;
    }

    // ── C1: lock del backfill ──────────────────────────────────────────────

    [Fact]
    public async Task Backfill_ConcurrentRun_Gets409_AndEachPhotoIsDownloadedOnce()
    {
        fixture.FakeR2.Configured = true;
        var client = CreateAdminClient();
        var db = fixture.GetDbContext();
        await ClearPlacesAsync(db);
        SeedPlace(db, "C1 Place", [GoogleUrl("c1-photo")]);
        await db.SaveChangesAsync();

        // Primera request: la descarga queda EN VUELO (bloqueada) — el barrido está corriendo.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.FakePhotos.AsyncResponder = async (_, ct) =>
        {
            await gate.Task.WaitAsync(ct);
            return FakePhotoHandler.DefaultOk();
        };

        var first = client.PostAsync("/admin/places/backfill-photos?limit=200", content: null);
        await WaitUntilAsync(() => CallCount("c1-photo") >= 1);

        // Segunda request concurrente: 409 inmediato, sin encolarse ni descargar nada.
        var second = await client.PostAsync("/admin/places/backfill-photos?limit=200", content: null);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("backfill_already_running", secondBody.GetProperty("error").GetString());

        gate.SetResult();
        var firstResponse = await first;
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

        // La foto se descargó (y facturó) exactamente UNA vez.
        Assert.Equal(1, CallCount("c1-photo"));

        // Con el lock libre, un run posterior vuelve a funcionar (no queda retenido).
        fixture.FakePhotos.AsyncResponder = null;
        var third = await client.PostAsync("/admin/places/backfill-photos?limit=200", content: null);
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
    }

    // ── M1: convergencia sin livelock ──────────────────────────────────────

    [Fact]
    public async Task Backfill_PersistentSourceFailure_DoesNotStarveLaterPlaces_AndConverges()
    {
        fixture.FakeR2.Configured = true;
        var client = CreateAdminClient();
        var db = fixture.GetDbContext();
        await ClearPlacesAsync(db);

        // El place MÁS ANTIGUO falla siempre (URL de Google muerta: key rotada → 404).
        // Con limit=1 y orden por CreatedAt, sin deferral ocuparía la única plaza de la
        // página en TODOS los runs y B/C no migrarían jamás (livelock del runbook).
        var t0 = DateTimeOffset.UtcNow.AddDays(-3);
        SeedPlace(db, "M1 Poison", [GoogleUrl("m1-poison")], createdAt: t0);
        SeedPlace(db, "M1 Behind B", [GoogleUrl("m1-b")], createdAt: t0.AddHours(1));
        SeedPlace(db, "M1 Behind C", [GoogleUrl("m1-c")], createdAt: t0.AddHours(2));
        await db.SaveChangesAsync();

        fixture.FakePhotos.Responder = req =>
            req.RequestUri!.ToString().Contains("m1-poison")
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : FakePhotoHandler.DefaultOk();

        JsonElement last = default;
        var runs = 0;
        do
        {
            var response = await client.PostAsync("/admin/places/backfill-photos?limit=1", content: null);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            last = await response.Content.ReadFromJsonAsync<JsonElement>();
            runs++;
        } while (last.GetProperty("remainingPlaces").GetInt32() > 0 && runs < 6);

        // Converge: remainingPlaces llega a 0 en pocos runs pese al fallo permanente.
        Assert.Equal(0, last.GetProperty("remainingPlaces").GetInt32());
        Assert.True(runs <= 4, $"backfill needed {runs} runs to converge");
        // El fallido queda reportado como diferido, no desaparece en silencio.
        Assert.Equal(1, last.GetProperty("deferredPlaces").GetInt32());

        // Los places de detrás migraron de verdad.
        var freshDb = fixture.GetDbContext();
        var b = await freshDb.Places.AsNoTracking().SingleAsync(p => p.Name == "M1 Behind B");
        var c = await freshDb.Places.AsNoTracking().SingleAsync(p => p.Name == "M1 Behind C");
        Assert.StartsWith(R2PublicUrl, Assert.Single(b.Photos!));
        Assert.StartsWith(R2PublicUrl, Assert.Single(c.Photos!));

        // El envenenado conserva su original (reintento futuro) y solo se facturó UNA vez:
        // el backoff evita re-descargar una fuente rota en cada run del bucle.
        var poison = await freshDb.Places.AsNoTracking().SingleAsync(p => p.Name == "M1 Poison");
        Assert.Equal(new List<string> { GoogleUrl("m1-poison") }, poison.Photos);
        Assert.Equal(1, CallCount("m1-poison"));

        // retryDeferred=true reintenta manualmente al diferido (sin esperar el backoff).
        var retry = await client.PostAsync("/admin/places/backfill-photos?limit=10&retryDeferred=true", content: null);
        var retryBody = await retry.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, retryBody.GetProperty("processedPlaces").GetInt32());
        Assert.Equal(2, CallCount("m1-poison"));
    }

    // ── M2: lost update backfill ↔ PATCH ───────────────────────────────────

    [Fact]
    public async Task Backfill_DoesNotOverwriteConcurrentAdminPatch()
    {
        fixture.FakeR2.Configured = true;
        var client = CreateAdminClient();
        var db = fixture.GetDbContext();
        await ClearPlacesAsync(db);
        var place = SeedPlace(db, "M2 Race Place", [GoogleUrl("m2-slow")]);
        await db.SaveChangesAsync();

        // El backfill queda con la descarga EN VUELO…
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.FakePhotos.AsyncResponder = async (req, ct) =>
        {
            if (req.RequestUri!.ToString().Contains("m2-slow"))
                await gate.Task.WaitAsync(ct);
            return FakePhotoHandler.DefaultOk();
        };

        var backfillTask = client.PostAsync("/admin/places/backfill-photos?limit=200", content: null);
        await WaitUntilAsync(() => CallCount("m2-slow") >= 1);

        // …y un admin PATCHea las fotos del mismo place (URL ya en R2 → sin descarga, commit ya).
        var patchedPhoto = $"{R2PublicUrl}/places/m2-patched-{Guid.NewGuid():N}.webp";
        var patch = await client.PatchAsJsonAsync($"/admin/places/{place.Id}",
            new { photos = new[] { patchedPhoto } });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);

        gate.SetResult();
        var backfillResponse = await backfillTask;
        Assert.Equal(HttpStatusCode.OK, backfillResponse.StatusCode);
        var body = await backfillResponse.Content.ReadFromJsonAsync<JsonElement>();

        // El PATCH nunca se pisa: el token xmin fuerza reload+merge en el backfill y la URL
        // vieja de Google ya no está presente, así que no se aplica nada.
        var freshDb = fixture.GetDbContext();
        var final = await freshDb.Places.AsNoTracking().SingleAsync(p => p.Id == place.Id);
        Assert.Equal(new List<string> { patchedPhoto }, final.Photos);
        Assert.Equal(1, body.GetProperty("conflictPlaces").GetInt32());
    }

    [Fact]
    public async Task AdminPatch_StaleAfterBackfillMigration_Returns409_InsteadOfSilentOverwrite()
    {
        fixture.FakeR2.Configured = true;
        var client = CreateAdminClient();
        var db = fixture.GetDbContext();
        await ClearPlacesAsync(db);
        var place = SeedPlace(db, "M2 Stale Patch Place", [GoogleUrl("m2b-original")]);
        await db.SaveChangesAsync();

        // El PATCH queda a mitad (su rehost de una foto nueva está bloqueado)…
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.FakePhotos.AsyncResponder = async (req, ct) =>
        {
            if (req.RequestUri!.ToString().Contains("m2b-block"))
                await gate.Task.WaitAsync(ct);
            return FakePhotoHandler.DefaultOk();
        };

        var patchTask = client.PatchAsJsonAsync($"/admin/places/{place.Id}",
            new { photos = new[] { WanderlogUrl("m2b-block") } });
        await WaitUntilAsync(() => CallCount("m2b-block") >= 1);

        // …mientras el backfill migra el place y commitea (xmin avanza).
        var backfill = await client.PostAsync("/admin/places/backfill-photos?limit=200", content: null);
        Assert.Equal(HttpStatusCode.OK, backfill.StatusCode);

        gate.SetResult();
        var patchResponse = await patchTask;

        // El PATCH stale NO machaca la migración en silencio: 409 y el admin recarga.
        Assert.Equal(HttpStatusCode.Conflict, patchResponse.StatusCode);
        var patchBody = await patchResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("concurrent_update", patchBody.GetProperty("error").GetString());

        var freshDb = fixture.GetDbContext();
        var final = await freshDb.Places.AsNoTracking().SingleAsync(p => p.Id == place.Id);
        var photo = Assert.Single(final.Photos!);
        Assert.StartsWith($"{R2PublicUrl}/places/", photo);
    }

    // ── M3: circuit breaker de uploads a R2 ────────────────────────────────

    [Fact]
    public async Task Backfill_R2UploadDown_AbortsSweep_InsteadOfBillingGoogleForNothing()
    {
        fixture.FakeR2.Configured = true;
        fixture.FakeR2.UploadFailure = _ => new InvalidOperationException("simulated R2 5xx");
        var client = CreateAdminClient();
        var db = fixture.GetDbContext();
        await ClearPlacesAsync(db);
        for (var i = 0; i < 5; i++)
            SeedPlace(db, $"M3 Place {i}", [GoogleUrl($"m3-{i}")]);
        await db.SaveChangesAsync();

        var response = await client.PostAsync("/admin/places/backfill-photos?limit=200", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Aborta tras 3 fallos consecutivos de upload: solo 3 descargas facturadas, no 5.
        Assert.True(body.GetProperty("aborted").GetBoolean());
        Assert.Equal("r2_upload_unavailable", body.GetProperty("abortReason").GetString());
        Assert.Equal(3, fixture.FakePhotos.Calls.Count);
        Assert.True(body.GetProperty("remainingPlaces").GetInt32() > 0);
        // Un R2 caído no es culpa de los places: NINGUNO entra en backoff.
        Assert.Equal(0, body.GetProperty("deferredPlaces").GetInt32());
        Assert.Equal(0, body.GetProperty("failedPlaces").GetInt32());

        // R2 vuelve → el siguiente run migra los 5 sin residuos del incidente.
        fixture.FakeR2.UploadFailure = null;
        var second = await client.PostAsync("/admin/places/backfill-photos?limit=200", content: null);
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(secondBody.GetProperty("aborted").GetBoolean());
        Assert.Equal(5, secondBody.GetProperty("updatedPlaces").GetInt32());
        Assert.Equal(0, secondBody.GetProperty("remainingPlaces").GetInt32());
    }

    // ── M4: R2 colgado no tumba la creación de places ──────────────────────

    [Fact]
    public async Task CreatePlace_R2UploadTimeout_StillCreatesPlace_DegradingPhotos()
    {
        fixture.FakeR2.Configured = true;
        // El timeout interno del SDK S3 aflora como TaskCanceledException SIN que el ct
        // de la request esté cancelado — antes del fix propagaba y abortaba la creación.
        fixture.FakeR2.UploadFailure = _ => new TaskCanceledException("simulated S3 client timeout");
        var client = CreateAdminClient();
        var name = $"M4 Hung R2 {Guid.NewGuid():N}";

        var response = await client.PostAsJsonAsync("/admin/places", new
        {
            name,
            category = "Food",
            whyThisPlace = "test",
            city = "Miami",
            photos = new[] { GoogleUrl("m4-google"), WanderlogUrl("m4-hotlink") },
        });

        // El place SE CREA; la foto de Google (con key) se descarta y el hotlink sin key
        // se conserva para que el backfill lo migre cuando R2 vuelva.
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var db = fixture.GetDbContext();
        var place = await db.Places.AsNoTracking().SingleAsync(p => p.Name == name);
        Assert.Equal(new List<string> { WanderlogUrl("m4-hotlink") }, place.Photos);
    }

    // ── M5: import masivo — abort del caller a mitad no pierde nada ni revienta ──

    [Fact]
    public async Task BulkImport_CallerAbortedMidway_PersistsEverything_NoException()
    {
        // M5 + MAJOR ronda 4: el proxy de Railway corta la request (RequestAborted) a mitad del
        // import. Antes: el OperationCanceledException del caller propagaba y reventaba el bulk;
        // el commit por chunks salvaba SÓLO el primer chunk (10 de 15). Ahora, con la degradación
        // INCONDICIONAL del rehost + el commit del chunk con un CT que sobrevive el abort, la
        // request NO revienta y se persisten los 15 places: los primeros con su foto ya rehosteada
        // a R2, los del punto de corte en adelante con su URL original (sin key → conservable para
        // el backfill). Nada del gasto ya incurrido se pierde.
        fixture.FakeR2.Configured = true;
        var prefix = $"M5-{Guid.NewGuid():N}";

        // 15 places con 1 foto cada uno; la foto del 11º (índice 10, ya en el segundo chunk)
        // dispara el abort del caller — simula el corte del proxy a mitad de import.
        using var cts = new CancellationTokenSource();
        fixture.FakePhotos.AsyncResponder = (req, ct) =>
        {
            if (req.RequestUri!.ToString().Contains($"{prefix}-10"))
            {
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
            }
            return Task.FromResult(FakePhotoHandler.DefaultOk());
        };

        var requests = Enumerable.Range(0, 15).Select(i => new CreatePlaceRequest
        {
            Name = $"{prefix} Place {i:D2}",
            Category = "Food",
            WhyThisPlace = "bulk test",
            City = "Miami",
            Photos = [WanderlogUrl($"{prefix}-{i}")],
        }).ToList();

        using var scope = fixture.Services.CreateScope();
        var importSvc = scope.ServiceProvider.GetRequiredService<PlaceImportService>();

        // NO lanza: el abort del caller degrada el rehost en vez de propagar.
        var result = await importSvc.BulkImportAsync(
            requests, null, fixture.FakeTime.GetUtcNow(), cts.Token);
        Assert.Equal(15, result.Created);

        // Los 15 quedan persistidos (chunks commiteados con CancellationToken.None): 0-9 migrados a
        // R2, 10-14 con su original de wanderlog. Antes del fix: excepción + sólo 10 filas.
        var db = fixture.GetDbContext();
        var persisted = await db.Places.AsNoTracking()
            .Where(p => p.Name.StartsWith(prefix))
            .OrderBy(p => p.Name)
            .ToListAsync();
        Assert.Equal(15, persisted.Count);
        Assert.All(persisted, p => Assert.NotNull(p.Photos));
        var migrated = persisted.Count(p => p.Photos!.Single().StartsWith($"{R2PublicUrl}/places/"));
        var keptOriginal = persisted.Count(p => p.Photos!.Single().StartsWith("https://wanderlog.com/"));
        Assert.Equal(10, migrated);
        Assert.Equal(5, keptOriginal);
    }

    // ── MAJOR (ronda 4): abort del CALLER (no del budget) durante R2 colgado ────

    [Fact]
    public async Task REPRO_BulkImport_PreWorkAteDeadline_R2Hung_CallerAbort_CreatesAllWithoutPhoto_NoException()
    {
        // Invariante (d), el fallo que el enfoque budget-only NO cubría (rondas 2-3). El pre-work
        // de la request (descripciones Gemini + 2 llamadas Google × N URLs) consume la MAYORÍA del
        // deadline de 40s del proxy de Railway → cuando arranca el rehost quedan pocos ms de
        // RequestAborted, así que con R2 colgado el ct del CALLER se dispara ANTES que el
        // presupuesto de wall-clock (que aquí se deja en su DEFAULT de 25s — largo, NO es el
        // mecanismo del corte). Antes: ese OperationCanceledException del caller propagaba por
        // RehostForIngestAsync → InsertWithDedupAsync y reventaba el bulk (chunk perdido, Google ya
        // facturado). Ahora la garantía es INCONDICIONAL: (1) el rehost degrada da igual la fuente
        // del ct y (2) el commit del chunk usa un CT que sobrevive el abort → todos los places se
        // crean sin foto, la request NO revienta, sin excepción propagada.
        fixture.FakeR2.Configured = true;
        var prefix = $"R4-{Guid.NewGuid():N}";

        // Modelamos "el pre-work agotó el deadline": el ct del caller (RequestAborted) se cancela
        // en el PRIMER upload a R2 (el componente colgado). El presupuesto queda en su default
        // largo — si el fix dependiera del budget, este test seguiría reventando.
        using var cts = new CancellationTokenSource();
        fixture.FakeR2.UploadFailure = _ =>
        {
            cts.Cancel(); // el proxy corta la request a mitad del upload al R2 colgado
            return new TaskCanceledException("proxy aborted mid-upload on hung R2");
        };

        // 12 places con foto de Google (con key): al degradar, la key se descarta → Photos=null.
        var requests = Enumerable.Range(0, 12).Select(i => new CreatePlaceRequest
        {
            Name = $"{prefix} Place {i:D2}",
            Category = "Food",
            WhyThisPlace = "bulk test",
            City = "Miami",
            Photos = [GoogleUrl($"{prefix}-{i}")],
        }).ToList();

        using var scope = fixture.Services.CreateScope();
        var importSvc = scope.ServiceProvider.GetRequiredService<PlaceImportService>();

        // Presupuesto en DEFAULT (no se toca con WithIngestRehostBudget): el corte lo provoca el
        // caller, no el budget. NO lanza pese al abort del proxy.
        var result = await importSvc.BulkImportAsync(
            requests, null, fixture.FakeTime.GetUtcNow(), cts.Token);
        Assert.Equal(12, result.Created);

        // TODOS los places creados (chunks commiteados con un CT que sobrevive el abort) y SIN foto
        // (la URL de Google con key jamás se persiste al degradar).
        var db = fixture.GetDbContext();
        var persisted = await db.Places.AsNoTracking()
            .Where(p => p.Name.StartsWith(prefix)).ToListAsync();
        Assert.Equal(12, persisted.Count);
        Assert.All(persisted, p => Assert.Null(p.Photos));

        // Sólo el primer place llegó a intentar el upload (que canceló al caller); el resto degradó
        // vía breaker.IsOpen sin descargar de Google. El presupuesto default nunca entró en juego.
        Assert.Equal(1, fixture.FakePhotos.Calls.Count);
    }

    // ── M6: recuperación vía GooglePlaceId ─────────────────────────────────

    [Fact]
    public async Task Backfill_RecoverMissing_RefetchesPhotosViaGooglePlaceId()
    {
        fixture.FakeR2.Configured = true;
        var client = CreateAdminClient();
        var db = fixture.GetDbContext();
        await ClearPlacesAsync(db);

        // Place cuya ingesta perdió las fotos (fallo transitorio → Photos=null) pero con
        // GooglePlaceId: recuperable re-obteniendo los photo refs de Places Details.
        var recoverable = SeedPlace(db, "M6 Recoverable", photos: null);
        recoverable.GooglePlaceId = "m6-recoverable";
        // Y otro cuyo Details no devuelve nada — no debe reintentarse en cada barrido.
        var unrecoverable = SeedPlace(db, "M6 Unrecoverable", photos: null);
        unrecoverable.GooglePlaceId = "m6-unrecoverable";
        await db.SaveChangesAsync();

        fixture.FakeGooglePlaces.DetailsByPlaceId["m6-recoverable"] = new GooglePlaceDetails(
            Id: "m6-recoverable", Name: "M6 Recoverable", FormattedAddress: "Addr", City: "Miami",
            Neighborhood: null, Lat: 25.76m, Lng: -80.19m, PrimaryType: "restaurant",
            Types: ["restaurant"], PriceLevel: "$$",
            Photos: [GoogleUrl("m6-a"), GoogleUrl("m6-b")],
            Rating: 4.5m, ReviewCount: 10, Website: null, Phone: null, EditorialSummary: null);

        // Sin recoverMissing, esos places solo se reportan (no se factura Details).
        var plain = await client.PostAsync("/admin/places/backfill-photos?limit=200", content: null);
        var plainBody = await plain.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, plainBody.GetProperty("missingPhotoPlaces").GetInt32());
        Assert.Equal(0, plainBody.GetProperty("recoveredPlaces").GetInt32());

        var response = await client.PostAsync(
            "/admin/places/backfill-photos?limit=200&recoverMissing=true", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(1, body.GetProperty("recoveredPlaces").GetInt32());
        Assert.Equal(0, body.GetProperty("remainingMissingPlaces").GetInt32());

        var freshDb = fixture.GetDbContext();
        var recovered = await freshDb.Places.AsNoTracking().SingleAsync(p => p.Id == recoverable.Id);
        Assert.Equal(2, recovered.Photos!.Count);
        Assert.All(recovered.Photos, p =>
        {
            Assert.StartsWith($"{R2PublicUrl}/places/", p);
            Assert.DoesNotContain("key=", p);
        });

        // El irrecuperable queda diferido: un segundo run recoverMissing no re-factura Details.
        var second = await client.PostAsync(
            "/admin/places/backfill-photos?limit=200&recoverMissing=true", content: null);
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, secondBody.GetProperty("remainingMissingPlaces").GetInt32());
        Assert.Equal(0, secondBody.GetProperty("recoveredPlaces").GetInt32());
    }

    // ── m3: conflicto xmin en RECOVERY → backoff, no re-factura Details ─────

    [Fact]
    public async Task Backfill_RecoveryConflict_DefersPlace_DoesNotRebillGoogleDetailsNextRun()
    {
        fixture.FakeR2.Configured = true;
        var client = CreateAdminClient();
        var db = fixture.GetDbContext();
        await ClearPlacesAsync(db);

        // Place sin fotos pero con GooglePlaceId → recuperable vía Places Details.
        var place = SeedPlace(db, "m3 Recovery Conflict", photos: null);
        place.GooglePlaceId = "m3rec";
        await db.SaveChangesAsync();

        fixture.FakeGooglePlaces.DetailsByPlaceId["m3rec"] = new GooglePlaceDetails(
            Id: "m3rec", Name: "m3 Recovery Conflict", FormattedAddress: "Addr", City: "Miami",
            Neighborhood: null, Lat: 25.76m, Lng: -80.19m, PrimaryType: "restaurant",
            Types: ["restaurant"], PriceLevel: "$$", Photos: [GoogleUrl("m3rec-a")],
            Rating: 4.5m, ReviewCount: 10, Website: null, Phone: null, EditorialSummary: null);

        // La descarga de recovery queda EN VUELO mientras otro escritor bumpea la fila (xmin++),
        // forzando el conflicto xmin en el TrySaveChangesAsync de la recuperación.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.FakePhotos.AsyncResponder = async (req, ct) =>
        {
            if (req.RequestUri!.ToString().Contains("m3rec-a"))
                await gate.Task.WaitAsync(ct);
            return FakePhotoHandler.DefaultOk();
        };

        var backfillTask = client.PostAsync(
            "/admin/places/backfill-photos?limit=200&recoverMissing=true", content: null);
        await WaitUntilAsync(() => CallCount("m3rec-a") >= 1);

        var other = fixture.GetDbContext();
        var otherPlace = await other.Places.FirstAsync(p => p.Id == place.Id);
        otherPlace.WhyThisPlace = "bumped by concurrent writer";
        await other.SaveChangesAsync();

        gate.SetResult();
        var response = await backfillTask;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // La recuperación chocó (xmin stale): no se recuperó y se contó el conflicto.
        Assert.Equal(0, body.GetProperty("recoveredPlaces").GetInt32());
        Assert.Equal(1, body.GetProperty("conflictPlaces").GetInt32());
        Assert.Equal(1, CallCount("m3rec-a")); // Details se facturó y la foto se descargó UNA vez.

        // m3: el place entró en backoff → un re-run recoverMissing SIN retryDeferred NO vuelve a
        // facturar Google Details ni re-descarga (antes: se descartaba en silencio + detach, sin
        // RecordFailure → el place re-facturaba Details en el siguiente barrido).
        var second = await client.PostAsync(
            "/admin/places/backfill-photos?limit=200&recoverMissing=true", content: null);
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, secondBody.GetProperty("recoveredPlaces").GetInt32());
        Assert.Equal(1, CallCount("m3rec-a")); // sigue en 1: no re-descargó → no re-facturó Details.

        // La fila sigue sin fotos (el conflicto no la corrompió) — recuperable con retryDeferred.
        var freshDb = fixture.GetDbContext();
        var stillMissing = await freshDb.Places.AsNoTracking().SingleAsync(p => p.Id == place.Id);
        Assert.Null(stillMissing.Photos);
    }

    // ── m1: la key de Google no sale por la API admin ──────────────────────

    [Fact]
    public async Task AdminPlaceDto_NeverExposesUrlsWithApiKey()
    {
        var db = fixture.GetDbContext();
        var r2Photo = $"{R2PublicUrl}/places/m-admin-{Guid.NewGuid():N}.webp";
        var place = SeedPlace(db, $"m1 Admin Guard {Guid.NewGuid():N}",
            [GoogleUrl("m1-admin"), r2Photo]);
        await db.SaveChangesAsync();

        var client = CreateAdminClient();

        var detail = await client.GetAsync($"/admin/places/{place.Id}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var detailRaw = await detail.Content.ReadAsStringAsync();
        Assert.DoesNotContain("key=", detailRaw);
        var photos = JsonDocument.Parse(detailRaw).RootElement.GetProperty("photos");
        Assert.Equal(r2Photo, Assert.Single(photos.EnumerateArray().ToList()).GetString());

        var list = await client.GetAsync("/admin/places?limit=200");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.DoesNotContain("key=", await list.Content.ReadAsStringAsync());
    }

    // ── m2: SSRF — host fuera de allowlist ni siquiera se descarga ─────────

    [Fact]
    public async Task Backfill_NonAllowlistedHost_IsNeverFetched_AndDeferred()
    {
        fixture.FakeR2.Configured = true;
        var client = CreateAdminClient();
        var db = fixture.GetDbContext();
        await ClearPlacesAsync(db);
        var blockedUrl = "https://cdn.example.com/internal/photo.jpg";
        SeedPlace(db, "m2 Blocked Host", [blockedUrl]);
        await db.SaveChangesAsync();

        var response = await client.PostAsync("/admin/places/backfill-photos?limit=200", content: null);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Cero HTTP salientes: la URL se bloquea ANTES de tocar la red.
        Assert.Empty(fixture.FakePhotos.Calls);
        Assert.Equal(1, body.GetProperty("failedPlaces").GetInt32());
        Assert.Equal(1, body.GetProperty("census").GetProperty("other").GetProperty("failed").GetInt32());

        var freshDb = fixture.GetDbContext();
        var place = await freshDb.Places.AsNoTracking().SingleAsync(p => p.Name == "m2 Blocked Host");
        Assert.Equal(new List<string> { blockedUrl }, place.Photos);
    }

    // ── m4: deleted/rejected fuera de candidatos y censo ───────────────────

    [Fact]
    public async Task Backfill_ExcludesSoftDeletedAndRejectedPlaces()
    {
        fixture.FakeR2.Configured = true;
        var client = CreateAdminClient();
        var db = fixture.GetDbContext();
        await ClearPlacesAsync(db);

        SeedPlace(db, "m4 Published", [GoogleUrl("m4-published")]);
        SeedPlace(db, "m4 Deleted", [GoogleUrl("m4-deleted")], status: "deleted");
        SeedPlace(db, "m4 Rejected", [GoogleUrl("m4-rejected")], status: "rejected");
        await db.SaveChangesAsync();

        var response = await client.PostAsync("/admin/places/backfill-photos?limit=200", content: null);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Censo y candidatos solo cuentan el place vivo; no se paga por contenido muerto.
        Assert.Equal(1, body.GetProperty("totalPlacesWithPhotos").GetInt32());
        Assert.Equal(1, body.GetProperty("candidatePlaces").GetInt32());
        Assert.Equal(1, body.GetProperty("census").GetProperty("places.googleapis.com").GetProperty("photos").GetInt32());
        Assert.Equal(1, fixture.FakePhotos.Calls.Count);
        Assert.Equal(0, CallCount("m4-deleted"));
        Assert.Equal(0, CallCount("m4-rejected"));

        var freshDb = fixture.GetDbContext();
        var deleted = await freshDb.Places.AsNoTracking().SingleAsync(p => p.Name == "m4 Deleted");
        var rejected = await freshDb.Places.AsNoTracking().SingleAsync(p => p.Name == "m4 Rejected");
        Assert.Equal(new List<string> { GoogleUrl("m4-deleted") }, deleted.Photos);
        Assert.Equal(new List<string> { GoogleUrl("m4-rejected") }, rejected.Photos);
    }

    // ── G1: convergencia HONESTA (ronda 2) ────────────────────────────────

    [Fact]
    public async Task Backfill_TransientSourceFailureInRun_DoesNotReportConvergence()
    {
        fixture.FakeR2.Configured = true;
        var client = CreateAdminClient();
        var db = fixture.GetDbContext();
        await ClearPlacesAsync(db);
        SeedPlace(db, "G1 Transient", [GoogleUrl("g1-transient")]);
        await db.SaveChangesAsync();

        // La fuente devuelve un 503 TRANSITORIO durante este run (p. ej. Google throttling).
        // El place se difiere DESPUÉS de calcular remainingPlaces/deferredPlaces al estilo viejo,
        // así que el bug reportaba remaining=0 + deferred=0 + failed=1 → el operador leía las
        // dos señales de "terminado" del runbook y desplegaba con el place en blanco.
        fixture.FakePhotos.Responder = req =>
            req.RequestUri!.ToString().Contains("g1-transient")
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : FakePhotoHandler.DefaultOk();

        var response = await client.PostAsync("/admin/places/backfill-photos?limit=200", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Nada quedó SIN PROCESAR, pero el place migrable NO migró → la señal debe ser honesta:
        // converged=false, y el fallo se refleja en failedPlaces Y deferredPlaces (recalculado
        // al final del run, no un snapshot del inicio).
        Assert.Equal(0, body.GetProperty("remainingPlaces").GetInt32());
        Assert.Equal(1, body.GetProperty("failedPlaces").GetInt32());
        Assert.Equal(1, body.GetProperty("deferredPlaces").GetInt32());
        Assert.False(body.GetProperty("converged").GetBoolean());

        // La original (con key) se conserva para el reintento; el DTO público la filtra.
        var freshDb = fixture.GetDbContext();
        var place = await freshDb.Places.AsNoTracking().SingleAsync(p => p.Name == "G1 Transient");
        Assert.Equal(new List<string> { GoogleUrl("g1-transient") }, place.Photos);

        // El fallo era transitorio: la fuente se recupera y un re-run (retryDeferred, sin esperar
        // el backoff) migra el place y AHORA sí converge — imposible declarar convergencia con
        // un place migrable en blanco.
        fixture.FakePhotos.Responder = null;
        var retry = await client.PostAsync("/admin/places/backfill-photos?limit=200&retryDeferred=true", content: null);
        var retryBody = await retry.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, retryBody.GetProperty("updatedPlaces").GetInt32());
        Assert.Equal(0, retryBody.GetProperty("failedPlaces").GetInt32());
        Assert.Equal(0, retryBody.GetProperty("deferredPlaces").GetInt32());
        Assert.True(retryBody.GetProperty("converged").GetBoolean());

        var final = await fixture.GetDbContext().Places.AsNoTracking().SingleAsync(p => p.Name == "G1 Transient");
        Assert.StartsWith(R2PublicUrl, Assert.Single(final.Photos!));
    }

    [Fact]
    public async Task Backfill_CleanRun_ReportsConverged()
    {
        fixture.FakeR2.Configured = true;
        var client = CreateAdminClient();
        var db = fixture.GetDbContext();
        await ClearPlacesAsync(db);
        SeedPlace(db, "G1 Clean A", [GoogleUrl("g1-clean-a")]);
        SeedPlace(db, "G1 Clean B", [WanderlogUrl("g1-clean-b")]);
        await db.SaveChangesAsync();

        var response = await client.PostAsync("/admin/places/backfill-photos?limit=200", content: null);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Todo migró sin fallos: converged=true es la única señal para desplegar.
        Assert.Equal(2, body.GetProperty("updatedPlaces").GetInt32());
        Assert.Equal(0, body.GetProperty("remainingPlaces").GetInt32());
        Assert.Equal(0, body.GetProperty("failedPlaces").GetInt32());
        Assert.Equal(0, body.GetProperty("deferredPlaces").GetInt32());
        Assert.True(body.GetProperty("converged").GetBoolean());
    }

    [Fact]
    public async Task REPRO_Backfill_SubThresholdUploadFailure_DoesNotReportConvergence()
    {
        // MAJOR 1 (ronda 3): la ronda 2 arregló el fallo de FUENTE (503 → difiere → failedPlaces).
        // Pero un fallo de UPLOAD a R2 por debajo del umbral de aborto (streak < 3) NO aborta el
        // barrido, NO difiere el place (no es culpa de su fuente) y NO era failedPlaces → el place
        // quedaba processedPlaces++ con su URL de Google, con remaining=0 && failed=0 && deferred=0
        // → converged=true MENTÍA. El operador leía "convergido", desplegaba, y esa foto se saneaba
        // a blanco en B2C. Ahora cuenta como unmigratedPlaces → converged=false.
        fixture.FakeR2.Configured = true;
        fixture.FakeR2.UploadFailure = _ => new InvalidOperationException("simulated R2 5xx");
        var client = CreateAdminClient();
        var db = fixture.GetDbContext();
        await ClearPlacesAsync(db);
        SeedPlace(db, "G1sub Upload", [GoogleUrl("g1sub-upload")]);
        await db.SaveChangesAsync();

        var response = await client.PostAsync("/admin/places/backfill-photos?limit=200", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Un solo fallo de upload (streak=1<3): NO aborta, NO difiere, NO es failedPlaces por
        // fuente, NADA queda sin procesar…
        Assert.False(body.GetProperty("aborted").GetBoolean());
        Assert.Equal(0, body.GetProperty("failedPlaces").GetInt32());
        Assert.Equal(0, body.GetProperty("deferredPlaces").GetInt32());
        Assert.Equal(0, body.GetProperty("remainingPlaces").GetInt32());
        // …pero el place migrable NO migró → converged DEBE ser false y unmigratedPlaces contarlo.
        Assert.Equal(1, body.GetProperty("unmigratedPlaces").GetInt32());
        Assert.False(body.GetProperty("converged").GetBoolean());

        // La URL de Google original se conserva para el reintento; el DTO público la filtra.
        var freshDb = fixture.GetDbContext();
        var place = await freshDb.Places.AsNoTracking().SingleAsync(p => p.Name == "G1sub Upload");
        Assert.Equal(new List<string> { GoogleUrl("g1sub-upload") }, place.Photos);

        // El place NO se difirió: un re-run INMEDIATO (sin retryDeferred, sin esperar backoff) con
        // R2 sano migra y AHORA sí converge — imposible declarar convergencia con un place en blanco.
        fixture.FakeR2.UploadFailure = null;
        var retry = await client.PostAsync("/admin/places/backfill-photos?limit=200", content: null);
        var retryBody = await retry.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, retryBody.GetProperty("updatedPlaces").GetInt32());
        Assert.Equal(0, retryBody.GetProperty("unmigratedPlaces").GetInt32());
        Assert.True(retryBody.GetProperty("converged").GetBoolean());

        var final = await fixture.GetDbContext().Places.AsNoTracking().SingleAsync(p => p.Name == "G1sub Upload");
        Assert.StartsWith(R2PublicUrl, Assert.Single(final.Photos!));
    }

    // ── G2: circuit breaker de INGESTA (ronda 2) + presupuesto wall-clock (ronda 3) ──

    [Fact]
    public async Task BulkImport_R2Hung_CreatesAllPlacesWithoutPhoto_2xx_NoException()
    {
        fixture.FakeR2.Configured = true;
        // R2 COLGADO modelado con COSTE REAL por intento: cada upload tarda 1.5s (acepta TCP y no
        // responde) antes de aflorar como TaskCanceledException. En prod ese coste (~10-20s/foto)
        // agota el proxy de Railway (40s) con solo ~2 uploads — ANTES de que el 3er fallo abra el
        // breaker por-intentos. Por eso el breaker también acota TIEMPO: presupuesto de wall-clock
        // (aquí 250ms, fracción del "deadline"; en prod 25s < 40s). Al agotarse, se degrada a "sin
        // foto" con margen para commitear. (Antes: UploadLatency=30ms hacía caber los 3 intentos
        // trivialmente y ocultaba que el coste real revienta el presupuesto de tiempo.)
        fixture.FakeR2.UploadLatency = TimeSpan.FromMilliseconds(1500);
        fixture.FakeR2.UploadFailure = _ => new TaskCanceledException("simulated S3 client timeout on hung R2");
        using var _budget = WithIngestRehostBudget(TimeSpan.FromMilliseconds(250));

        var client = CreateAdminClient();
        var prefix = $"G2-{Guid.NewGuid():N}";
        var requests = Enumerable.Range(0, 12).Select(i => new
        {
            name = $"{prefix} Place {i:D2}",
            category = "Food",
            whyThisPlace = "bulk test",
            city = "Miami",
            photos = new[] { GoogleUrl($"{prefix}-{i}") },
        }).ToList();

        var response = await client.PostAsJsonAsync("/admin/places/bulk", requests);

        // La request NO revienta pese al R2 colgado: 2xx, sin excepción propagada.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(12, body.GetProperty("created").GetInt32());

        // TODOS los places se crean SIN foto (URLs de Google con key descartadas — jamás se
        // persiste una key ni se aborta la creación por culpa de R2).
        var db = fixture.GetDbContext();
        var persisted = await db.Places.AsNoTracking().Where(p => p.Name.StartsWith(prefix)).ToListAsync();
        Assert.Equal(12, persisted.Count);
        Assert.All(persisted, p => Assert.Null(p.Photos));

        // El presupuesto de wall-clock corta el sangrado ANTES del breaker por-intentos: con
        // budget=250ms y latency=1500ms el ÚNICO upload que se intenta (el del primer place) se
        // corta a los 250ms → TripBudget → el resto degrada sin descargar de Google. El valor es
        // determinista: EXACTAMENTE 1 descarga (m1: el `<3` viejo era correcto pero laxo — con
        // UploadLatency=30ms daban exactamente 3 y escondían que el tiempo real revienta el
        // deadline).
        Assert.Equal(1, fixture.FakePhotos.Calls.Count);
    }

    [Fact]
    public async Task BulkImport_R2Hung_KeepsNonKeyOriginals_ForLaterBackfill()
    {
        fixture.FakeR2.Configured = true;
        fixture.FakeR2.UploadLatency = TimeSpan.FromMilliseconds(1500);
        fixture.FakeR2.UploadFailure = _ => new TaskCanceledException("simulated S3 client timeout on hung R2");
        using var _budget = WithIngestRehostBudget(TimeSpan.FromMilliseconds(250));

        var client = CreateAdminClient();
        var prefix = $"G2b-{Guid.NewGuid():N}";
        // Fotos SIN key (wanderlog): con R2 colgado se degradan conservando la original para
        // que el backfill las migre cuando R2 vuelva — nunca se pierden ni tumban el import.
        var requests = Enumerable.Range(0, 8).Select(i => new
        {
            name = $"{prefix} Place {i:D2}",
            category = "Food",
            whyThisPlace = "bulk test",
            city = "Miami",
            photos = new[] { WanderlogUrl($"{prefix}-{i}") },
        }).ToList();

        var response = await client.PostAsJsonAsync("/admin/places/bulk", requests);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var db = fixture.GetDbContext();
        var persisted = await db.Places.AsNoTracking().Where(p => p.Name.StartsWith(prefix)).ToListAsync();
        Assert.Equal(8, persisted.Count);
        Assert.All(persisted, p => Assert.StartsWith("https://wanderlog.com/", Assert.Single(p.Photos!)));
        // El presupuesto de wall-clock corta en el primer upload (budget 250ms < latency 1500ms):
        // EXACTAMENTE 1 descarga, el resto conserva la original sin volver a intentar (m1).
        Assert.Equal(1, fixture.FakePhotos.Calls.Count);
    }

    // ── g3: writers de Place stale → 409, no 500 (ronda 2) ─────────────────

    [Fact]
    public async Task DeletePlace_StaleAfterConcurrentWrite_Returns409_InsteadOf500()
    {
        var ct = TestContext.Current.CancellationToken;
        var db = fixture.GetDbContext();
        await ClearPlacesAsync(db);
        var place = SeedPlace(db, "g3 Delete Race", [GoogleUrl("g3-del")]);
        await db.SaveChangesAsync(ct);

        // El contexto del controller carga el place (snapshot de xmin v1)…
        using var scope = fixture.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var scopedDb = sp.GetRequiredService<LocalListDbContext>();
        _ = await scopedDb.Places.Include(p => p.PlanStops).FirstAsync(p => p.Id == place.Id, ct);

        // …y OTRO writer (p. ej. el backfill-photos en bucle) toca la fila → xmin avanza a v2.
        var other = fixture.GetDbContext();
        var otherPlace = await other.Places.FirstAsync(p => p.Id == place.Id, ct);
        otherPlace.WhyThisPlace = "bumped by concurrent writer";
        await other.SaveChangesAsync(ct);

        // DeletePlace guarda con xmin v1 stale → antes: DbUpdateConcurrencyException = 500 seco.
        // Ahora: 409 concurrent_update graceful (el admin recarga y reintenta).
        var controller = ActivatorUtilities.CreateInstance<AdminPlacesController>(sp);
        var result = await controller.DeletePlace(place.Id, hard: false, ct);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(HttpStatusCode.Conflict, (HttpStatusCode)conflict.StatusCode!);
        Assert.Contains("concurrent_update", JsonSerializer.Serialize(conflict.Value));

        // La fila sigue viva (no se borró en silencio con estado stale).
        var freshDb = fixture.GetDbContext();
        var stillThere = await freshDb.Places.AsNoTracking().SingleAsync(p => p.Id == place.Id, ct);
        Assert.NotEqual("deleted", stillThere.Status);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private int CallCount(string urlFragment)
    {
        lock (fixture.FakePhotos.Calls)
        {
            return fixture.FakePhotos.Calls.Count(c => c.RequestUri!.ToString().Contains(urlFragment));
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition not met within timeout.");
            await Task.Delay(10);
        }
    }

    private static Place SeedPlace(
        LocalList.API.NET.Shared.Data.LocalListDbContext db,
        string name,
        List<string>? photos,
        string status = "published",
        DateTimeOffset? createdAt = null)
    {
        var place = new Place
        {
            Id = Guid.NewGuid(),
            Name = name,
            Category = "Food",
            City = "Miami",
            WhyThisPlace = "seeded",
            Status = status,
            Photos = photos,
        };
        if (createdAt.HasValue)
        {
            place.CreatedAt = createdAt.Value;
            place.UpdatedAt = createdAt.Value;
        }
        db.Places.Add(place);
        return place;
    }

    private static async Task ClearPlacesAsync(LocalList.API.NET.Shared.Data.LocalListDbContext db)
    {
        await db.Places.ExecuteDeleteAsync();
    }

    private HttpClient CreateAdminClient()
    {
        var adminEmail = $"admin-{Guid.NewGuid():N}@locallist.ai";
        var adminFbUid = $"fb-admin-{Guid.NewGuid():N}";

        var db = fixture.GetDbContext();
        db.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Email = adminEmail,
            FirebaseUid = adminFbUid,
            Role = "admin"
        });
        db.SaveChanges();

        var client = fixture.CreateClient();
        var token = fixture.CreateToken(adminFbUid, adminEmail);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
