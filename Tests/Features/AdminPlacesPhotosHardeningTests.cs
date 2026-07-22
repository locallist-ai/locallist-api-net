using System.Net.Http.Headers;
using System.Text.Json;
using LocalList.API.NET.Features.Admin.Places;
using LocalList.API.NET.Shared.Data.Entities;
using LocalList.API.NET.Shared.Photos;
using Microsoft.Extensions.DependencyInjection;

namespace LocalList.API.Tests.Features;

/// <summary>
/// Repros permanentes de los dos pases adversariales sobre fotos→R2 (2026-07-22), en verde
/// tras los fixes — si alguno vuelve a rojo, la regresión es exactamente la del review:
///
/// C1  backfill concurrente (lock + 409, descarga única) ·
/// M1  livelock/head-of-line del backfill (deferral con backoff → convergencia) ·
/// M2  lost update backfill↔PATCH (token xmin: merge en backfill, 409 en PATCH stale) ·
/// M3  circuit breaker de uploads a R2 (no quemar dinero de Google sin progreso) ·
/// M4  R2 colgado no tumba la creación de places ·
/// M5  import masivo con commit por chunks (cancelación no pierde lo completado) ·
/// M6  recuperación de fotos vía GooglePlaceId ·
/// m1  la key de Google no sale por la API admin ·
/// m2  SSRF: host fuera de allowlist ni siquiera se descarga ·
/// m4  places deleted/rejected fuera de candidatos y censo.
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

    // ── M5: import masivo con commit por chunks ────────────────────────────

    [Fact]
    public async Task BulkImport_CanceledMidway_PersistsCompletedChunks()
    {
        fixture.FakeR2.Configured = true;
        var prefix = $"M5-{Guid.NewGuid():N}";

        // 15 places con 1 foto cada uno; la foto del 11º (índice 10, ya en el segundo chunk)
        // dispara la cancelación — simula el corte del proxy de Railway a mitad de import.
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

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            importSvc.BulkImportAsync(requests, null, fixture.FakeTime.GetUtcNow(), cts.Token));

        // El primer chunk de 10 quedó commiteado — Google/R2 ya pagados NO se pierden y el
        // re-run dedupa por Name+City. Antes del fix: 0 filas y todo el gasto huérfano.
        var db = fixture.GetDbContext();
        var persisted = await db.Places.AsNoTracking()
            .Where(p => p.Name.StartsWith(prefix))
            .ToListAsync();
        Assert.Equal(10, persisted.Count);
        Assert.All(persisted, p => Assert.StartsWith($"{R2PublicUrl}/places/", Assert.Single(p.Photos!)));
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
