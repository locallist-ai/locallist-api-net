using System.Net.Http.Headers;
using System.Text.Json;
using LocalList.API.NET.Features.Admin.Places;
using LocalList.API.NET.Shared.Data.Entities;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;

namespace LocalList.API.Tests.Features;

/// <summary>
/// Integración del rehost de fotos a R2 (ApiFixture = Postgres real por Testcontainers;
/// solo el object store R2 y las descargas HTTP están fakeadas — política del repo).
///
/// Cubre: ingesta (create/bulk/import-from-urls) persiste solo URLs de R2;
/// degradación graceful sin credenciales; backfill idempotente con censo por dominio;
/// y la garantía a nivel DTO de que ningún PlaceDto.Photos sale con key=.
/// </summary>
public class AdminPlacesPhotosTests(ApiFixture fixture) : IClassFixture<ApiFixture>, IDisposable
{
    private const string R2PublicUrl = "https://pub-7f09e69b5b644703825b6068a05dee8f.r2.dev";

    private static string GoogleUrl(string photoId) =>
        $"https://places.googleapis.com/v1/places/x/photos/{photoId}/media?maxWidthPx=1600&key=TEST-SECRET-KEY";

    // Cada test parte del estado por defecto (R2 sin configurar) y lo restaura al salir.
    public void Dispose()
    {
        fixture.FakeR2.Reset();
        fixture.FakePhotos.Responder = null;
        fixture.FakeGooglePlaces.Reset();
    }

    // ── Ingesta ────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreatePlace_WithR2Configured_PersistsOnlyR2Urls()
    {
        fixture.FakeR2.Configured = true;
        var client = CreateAdminClient();
        var name = $"Rehost Create {Guid.NewGuid():N}";

        var response = await client.PostAsJsonAsync("/admin/places", new
        {
            name,
            category = "Food",
            whyThisPlace = "test",
            city = "Miami",
            photos = new[] { GoogleUrl("create-1"), GoogleUrl("create-2") },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var photos = body.GetProperty("photos").EnumerateArray().Select(p => p.GetString()!).ToList();

        Assert.Equal(2, photos.Count);
        Assert.All(photos, p =>
        {
            Assert.StartsWith($"{R2PublicUrl}/places/", p);
            Assert.EndsWith(".webp", p);
            Assert.DoesNotContain("key=", p);
        });

        // DB: lo persistido es exactamente lo rehosteado, y el objeto subido es webp <=1200px.
        var db = fixture.GetDbContext();
        var place = await db.Places.AsNoTracking().SingleAsync(p => p.Name == name);
        Assert.Equal(photos, place.Photos);
        Assert.Equal(2, fixture.FakeR2.Objects.Count);
        foreach (var bytes in fixture.FakeR2.Objects.Values)
        {
            Assert.IsType<WebpFormat>(Image.DetectFormat(bytes), exactMatch: false);
            Assert.True(Image.Identify(bytes).Width <= 1200);
        }
    }

    [Fact]
    public async Task CreatePlace_WithoutR2Config_KeepsOriginalUrl_Gracefully()
    {
        // FakeR2.Configured = false (default) — espejo de prod sin credenciales.
        var client = CreateAdminClient();
        var name = $"Graceful Create {Guid.NewGuid():N}";
        var original = GoogleUrl("graceful-1");

        var response = await client.PostAsJsonAsync("/admin/places", new
        {
            name,
            category = "Food",
            whyThisPlace = "test",
            city = "Miami",
            photos = new[] { original },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var db = fixture.GetDbContext();
        var place = await db.Places.AsNoTracking().SingleAsync(p => p.Name == name);
        Assert.Equal(new List<string> { original }, place.Photos);
        Assert.Empty(fixture.FakeR2.Objects);
    }

    [Fact]
    public async Task BulkImport_RehostsExternalHotlinks()
    {
        fixture.FakeR2.Configured = true;
        var client = CreateAdminClient();
        var name = $"Bulk Wanderlog {Guid.NewGuid():N}";

        var response = await client.PostAsJsonAsync("/admin/places/bulk", new[]
        {
            new
            {
                name,
                category = "Food",
                whyThisPlace = "bulk hotlink",
                city = "Miami",
                photos = new[] { $"https://wanderlog.com/photos/{Guid.NewGuid():N}.jpg" },
            },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var db = fixture.GetDbContext();
        var place = await db.Places.AsNoTracking().SingleAsync(p => p.Name == name);
        var photo = Assert.Single(place.Photos!);
        Assert.StartsWith($"{R2PublicUrl}/places/", photo);
    }

    [Fact]
    public async Task ImportFromUrls_PersistsOnlyR2Urls_NeverGoogleKeyUrls()
    {
        fixture.FakeR2.Configured = true;
        var client = CreateAdminClient();

        var placeId = $"ChIJphotos{Guid.NewGuid():N}";
        var mapsUrl = $"https://www.google.com/maps/place/photo-test-{Guid.NewGuid():N}";
        var name = $"Imported With Photos {Guid.NewGuid():N}";
        fixture.FakeGooglePlaces.ResolvedByUrl[mapsUrl] = placeId;
        fixture.FakeGooglePlaces.DetailsByPlaceId[placeId] = new GooglePlaceDetails(
            Id: placeId, Name: name, FormattedAddress: "Addr", City: "Miami",
            Neighborhood: null, Lat: 25.76m, Lng: -80.19m, PrimaryType: "restaurant",
            Types: ["restaurant"], PriceLevel: "$$",
            Photos: [GoogleUrl("import-1"), GoogleUrl("import-2")],
            Rating: 4.5m, ReviewCount: 100, Website: null, Phone: null,
            EditorialSummary: "Great spot");

        var response = await client.PostAsJsonAsync("/admin/places/import-from-urls", new
        {
            urls = new[] { mapsUrl },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("created").GetInt32());

        var db = fixture.GetDbContext();
        var place = await db.Places.AsNoTracking().SingleAsync(p => p.GooglePlaceId == placeId);
        Assert.NotNull(place.Photos);
        Assert.Equal(2, place.Photos!.Count);
        Assert.All(place.Photos, p => Assert.StartsWith($"{R2PublicUrl}/places/", p));
        Assert.All(place.Photos, p => Assert.DoesNotContain("key=", p));
    }

    [Fact]
    public async Task Ingest_GoogleUrlFailingRehost_IsDropped_NotPersistedWithKey()
    {
        fixture.FakeR2.Configured = true;
        fixture.FakePhotos.Responder = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        var client = CreateAdminClient();
        var name = $"Dropped Photo {Guid.NewGuid():N}";

        var response = await client.PostAsJsonAsync("/admin/places", new
        {
            name,
            category = "Food",
            whyThisPlace = "test",
            city = "Miami",
            photos = new[] { GoogleUrl("failing-1") },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var db = fixture.GetDbContext();
        var place = await db.Places.AsNoTracking().SingleAsync(p => p.Name == name);
        Assert.Null(place.Photos); // mejor sin foto que con la key persistida
    }

    // ── Backfill ───────────────────────────────────────────────────────────

    [Fact]
    public async Task BackfillPhotos_MigratesByDomain_ReportsCensus_AndIsIdempotent()
    {
        fixture.FakeR2.Configured = true;
        var client = CreateAdminClient();
        var db = fixture.GetDbContext();

        // El censo es global: limpiamos places para que los números sean deterministas.
        await ClearPlacesAsync(db);

        var alreadyHosted = $"{R2PublicUrl}/places/already-{Guid.NewGuid():N}.webp";
        SeedPlace(db, "Google Place", [GoogleUrl("bf-1"), GoogleUrl("bf-2")]);
        SeedPlace(db, "Wanderlog Place", ["https://wanderlog.com/photos/bf.jpg"]);
        SeedPlace(db, "Hosted Place", [alreadyHosted]);
        SeedPlace(db, "Other Place", ["https://cdn.example.com/photo.jpg"]);
        SeedPlace(db, "No Photos Place", null);
        await db.SaveChangesAsync();

        // Primer run — migra todo lo no-R2.
        var response = await client.PostAsync("/admin/places/backfill-photos?limit=200", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(body.GetProperty("r2Configured").GetBoolean());
        Assert.Equal(4, body.GetProperty("totalPlacesWithPhotos").GetInt32());
        Assert.Equal(3, body.GetProperty("candidatePlaces").GetInt32());
        Assert.Equal(3, body.GetProperty("processedPlaces").GetInt32());
        Assert.Equal(3, body.GetProperty("updatedPlaces").GetInt32());
        Assert.Equal(0, body.GetProperty("remainingPlaces").GetInt32());

        var census = body.GetProperty("census");
        AssertCensusRow(census, "places.googleapis.com", photos: 2, migrated: 2, failed: 0);
        AssertCensusRow(census, "wanderlog.com", photos: 1, migrated: 1, failed: 0);
        AssertCensusRow(census, "r2.dev", photos: 1, migrated: 0, failed: 0);
        AssertCensusRow(census, "other", photos: 1, migrated: 1, failed: 0);
        Assert.Equal(1, census.GetProperty("other").GetProperty("photos").GetInt32());
        Assert.Equal(1, body.GetProperty("otherDomains").GetProperty("cdn.example.com").GetInt32());

        // DB: ya no queda ninguna URL fuera de R2 (y la ya hosteada no se tocó).
        var freshDb = fixture.GetDbContext();
        var allPhotos = (await freshDb.Places.AsNoTracking().Where(p => p.Photos != null).ToListAsync())
            .SelectMany(p => p.Photos!)
            .ToList();
        Assert.Equal(5, allPhotos.Count);
        Assert.All(allPhotos, p => Assert.StartsWith(R2PublicUrl, p));
        Assert.Contains(alreadyHosted, allPhotos);

        // Segundo run — idempotente: nada que migrar, censo todo en r2.dev.
        var second = await client.PostAsync("/admin/places/backfill-photos?limit=200", content: null);
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, secondBody.GetProperty("candidatePlaces").GetInt32());
        Assert.Equal(0, secondBody.GetProperty("processedPlaces").GetInt32());
        AssertCensusRow(secondBody.GetProperty("census"), "r2.dev", photos: 5, migrated: 0, failed: 0);
        AssertCensusRow(secondBody.GetProperty("census"), "places.googleapis.com", photos: 0, migrated: 0, failed: 0);
    }

    [Fact]
    public async Task BackfillPhotos_FailedDownload_KeepsOriginalAndReportsFailure()
    {
        fixture.FakeR2.Configured = true;
        fixture.FakePhotos.Responder = _ => new HttpResponseMessage(HttpStatusCode.BadGateway);
        var client = CreateAdminClient();
        var db = fixture.GetDbContext();
        await ClearPlacesAsync(db);

        var googleUrl = GoogleUrl("bf-fail");
        SeedPlace(db, "Failing Backfill Place", [googleUrl]);
        await db.SaveChangesAsync();

        var response = await client.PostAsync("/admin/places/backfill-photos", content: null);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        AssertCensusRow(body.GetProperty("census"), "places.googleapis.com", photos: 1, migrated: 0, failed: 1);
        Assert.Equal(0, body.GetProperty("updatedPlaces").GetInt32());

        // La original se conserva para reintentar en el siguiente run…
        var freshDb = fixture.GetDbContext();
        var place = await freshDb.Places.AsNoTracking().SingleAsync(p => p.Name == "Failing Backfill Place");
        Assert.Equal(new List<string> { googleUrl }, place.Photos);

        // …pero el DTO público jamás la expone (garantía key=).
        var placeResponse = await fixture.CreateClient().GetAsync($"/places/{place.Id}");
        var raw = await placeResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("key=", raw);
    }

    [Fact]
    public async Task BackfillPhotos_WithoutR2Config_ReturnsCensusWithoutTouchingAnything()
    {
        // Sin credenciales el endpoint sigue siendo útil: devuelve el censo (dato del hub).
        var client = CreateAdminClient();
        var db = fixture.GetDbContext();
        await ClearPlacesAsync(db);

        var googleUrl = GoogleUrl("bf-nocfg");
        SeedPlace(db, "No Config Place", [googleUrl]);
        await db.SaveChangesAsync();

        var response = await client.PostAsync("/admin/places/backfill-photos", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(body.GetProperty("r2Configured").GetBoolean());
        Assert.Equal(0, body.GetProperty("processedPlaces").GetInt32());
        Assert.Equal(1, body.GetProperty("remainingPlaces").GetInt32());
        AssertCensusRow(body.GetProperty("census"), "places.googleapis.com", photos: 1, migrated: 0, failed: 0);

        var freshDb = fixture.GetDbContext();
        var place = await freshDb.Places.AsNoTracking().SingleAsync(p => p.Name == "No Config Place");
        Assert.Equal(new List<string> { googleUrl }, place.Photos);
    }

    [Fact]
    public async Task BackfillPhotos_DryRun_ComputesCensusWithoutModifying()
    {
        fixture.FakeR2.Configured = true;
        var client = CreateAdminClient();
        var db = fixture.GetDbContext();
        await ClearPlacesAsync(db);

        SeedPlace(db, "DryRun Place", [GoogleUrl("bf-dry")]);
        await db.SaveChangesAsync();

        var response = await client.PostAsync("/admin/places/backfill-photos?dryRun=true", content: null);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(body.GetProperty("dryRun").GetBoolean());
        Assert.Equal(1, body.GetProperty("candidatePlaces").GetInt32());
        Assert.Equal(0, body.GetProperty("processedPlaces").GetInt32());
        AssertCensusRow(body.GetProperty("census"), "places.googleapis.com", photos: 1, migrated: 0, failed: 0);
        Assert.Empty(fixture.FakeR2.Objects);
    }

    // ── Garantía a nivel DTO (nunca key= hacia el cliente) ─────────────────

    [Fact]
    public async Task PlaceDto_NeverExposesUrlsWithApiKey()
    {
        // Sembramos directamente en DB el peor caso: una URL de Google con key persistida
        // (estado legacy pre-backfill). El DTO público debe filtrarla SIEMPRE.
        var db = fixture.GetDbContext();
        var r2Photo = $"{R2PublicUrl}/places/dto-guard-{Guid.NewGuid():N}.webp";
        var place = SeedPlace(db, $"Dto Guard {Guid.NewGuid():N}",
            [GoogleUrl("dto-guard"), r2Photo]);
        await db.SaveChangesAsync();

        var client = fixture.CreateClient(); // anónimo — path de la app B2C

        // Detalle
        var detail = await client.GetAsync($"/places/{place.Id}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        var detailRaw = await detail.Content.ReadAsStringAsync();
        Assert.DoesNotContain("key=", detailRaw);
        var detailBody = JsonDocument.Parse(detailRaw).RootElement;
        var photo = Assert.Single(detailBody.GetProperty("photos").EnumerateArray().ToList());
        Assert.Equal(r2Photo, photo.GetString());

        // Listado
        var list = await client.GetAsync("/places?limit=100");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.DoesNotContain("key=", await list.Content.ReadAsStringAsync());
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static void AssertCensusRow(JsonElement census, string domain, int photos, int migrated, int failed)
    {
        var row = census.GetProperty(domain);
        Assert.Equal(photos, row.GetProperty("photos").GetInt32());
        Assert.Equal(migrated, row.GetProperty("migrated").GetInt32());
        Assert.Equal(failed, row.GetProperty("failed").GetInt32());
    }

    private static Place SeedPlace(LocalList.API.NET.Shared.Data.LocalListDbContext db, string name, List<string>? photos)
    {
        var place = new Place
        {
            Id = Guid.NewGuid(),
            Name = name,
            Category = "Food",
            City = "Miami",
            WhyThisPlace = "seeded",
            Status = "published",
            Photos = photos,
        };
        db.Places.Add(place);
        return place;
    }

    /// <summary>
    /// El censo del backfill es global — para assertions deterministas limpiamos los places
    /// creados por otros tests de esta clase (esta clase no crea plans/stops que referencien places).
    /// </summary>
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
