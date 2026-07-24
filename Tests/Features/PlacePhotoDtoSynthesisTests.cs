using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using LocalList.API.NET.Shared.Data.Entities;
using LocalList.API.NET.Shared.Dtos;

namespace LocalList.API.Tests.Features;

// ── T2: PlaceDto sintetiza la URL del proxy de fotos + photoSource ────────────────────
//
// El proxy runtime (T1: GET /places/{id}/photos/0) resuelve la key server-side. Este DTO
// NUNCA debe reemitir al cliente una URL places.googleapis.com (lleva la key en el query
// string): si el Place tiene GooglePlaceId, se sintetiza la URL del proxy; si no, solo
// pasan URLs externas no-Google guardadas tal cual.

/// <summary>Fixture con <c>Api:PublicBaseUrl</c> configurada (simula prod/Railway).</summary>
public sealed class PlacePhotoBaseUrlFixture : ApiFixture
{
    public const string PublicBaseUrl = "https://api.test.locallist.ai";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Api:PublicBaseUrl", PublicBaseUrl);
        base.ConfigureWebHost(builder);
    }
}

internal static class PhotoDtoTestData
{
    /// <summary>URL con key tal y como la guardaba GooglePlacesService.ResolvePhotos ANTES de
    /// T3/T4. Se usa para simular datos legacy "sucios" que el DTO NUNCA debe reemitir.</summary>
    public const string StoredGoogleUrlWithKey =
        "https://places.googleapis.com/v1/places/ABC/photos/XYZ/media?maxWidthPx=1600&key=SUPER-SECRET-KEY";

    public static Place MakePlace(
        string name,
        string? googlePlaceId = null,
        List<string>? photos = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Category = "Food",
        City = "Miami",
        WhyThisPlace = "Great spot",
        Status = "published",
        GooglePlaceId = googlePlaceId,
        Photos = photos,
    };
}

// ── Defensa en profundidad: [JsonIgnore] sobre Place.Photos ───────────────────────────
//
// Cualquier endpoint que llegue a serializar una entidad Place CRUDA (path presente o futuro)
// NO debe emitir la key de Google guardada en Photos. JsonIgnore bloquea la serializacion pero
// NO la lectura en C#, asi que PlaceDto.FromEntity sigue sintetizando la foto por el proxy.
public class PlaceEntityJsonIgnorePhotosTests
{
    [Fact]
    public void RawPlaceEntity_SerializedToJson_OmitsPhotos_NeverLeaksKey()
    {
        var place = PhotoDtoTestData.MakePlace(
            "Raw Entity Spot",
            googlePlaceId: "ChIJ_raw_entity",
            photos: [PhotoDtoTestData.StoredGoogleUrlWithKey]);

        var json = JsonSerializer.Serialize(place, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        // [JsonIgnore] elimina Photos de la serializacion (en cualquier casing).
        Assert.DoesNotContain("photos", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("googleapis.com", json);
        Assert.DoesNotContain("key=", json);
        Assert.DoesNotContain("SUPER-SECRET-KEY", json);
    }

    [Fact]
    public void PlaceDto_FromSamePlace_StillSynthesizesProxyPhoto()
    {
        // La lectura en C# no se ve afectada por [JsonIgnore]: la foto legitima sigue saliendo,
        // sintetizada por el proxy (ruta relativa sin PublicBaseUrl).
        var place = PhotoDtoTestData.MakePlace(
            "Raw Entity Spot",
            googlePlaceId: "ChIJ_raw_entity",
            photos: [PhotoDtoTestData.StoredGoogleUrlWithKey]);

        var dto = PlaceDto.FromEntity(place, "en", publicBaseUrl: null);

        Assert.NotNull(dto.Photos);
        Assert.Equal(new[] { $"/places/{place.Id}/photos/0" }, dto.Photos);
        Assert.Equal("google", dto.PhotoSource);
    }
}

// ── Con Api:PublicBaseUrl configurada -> URL absoluta ─────────────────────────────────

public class PlaceDtoPhotoSynthesis_WithPublicBaseUrlTests : IClassFixture<PlacePhotoBaseUrlFixture>
{
    private readonly PlacePhotoBaseUrlFixture _fixture;
    public PlaceDtoPhotoSynthesis_WithPublicBaseUrlTests(PlacePhotoBaseUrlFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task GetPlace_WithGooglePlaceId_ReturnsAbsoluteProxyUrl_SourceGoogle()
    {
        var place = PhotoDtoTestData.MakePlace("Google Spot", googlePlaceId: "ChIJ_dto_abs");
        var db = _fixture.GetDbContext();
        db.Places.Add(place);
        await db.SaveChangesAsync();

        var client = _fixture.CreateClient();
        var response = await client.GetAsync($"/places/{place.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var photos = body.GetProperty("photos").EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.Equal(
            new[] { $"{PlacePhotoBaseUrlFixture.PublicBaseUrl}/places/{place.Id}/photos/0" },
            photos);
        Assert.Equal("google", body.GetProperty("photoSource").GetString());
    }

    [Fact]
    public async Task GetPlace_WithGooglePlaceId_AndStoredKeyedUrl_NeverReemitsStoredUrl()
    {
        // Datos "sucios" pre-T4: Photos guarda la URL con key. El DTO debe IGNORARLA por
        // completo y sintetizar solo el proxy, aunque la URL guardada siga en la fila.
        var place = PhotoDtoTestData.MakePlace(
            "Dirty Legacy Spot",
            googlePlaceId: "ChIJ_dto_dirty",
            photos: [PhotoDtoTestData.StoredGoogleUrlWithKey]);
        var db = _fixture.GetDbContext();
        db.Places.Add(place);
        await db.SaveChangesAsync();

        var client = _fixture.CreateClient();
        var response = await client.GetAsync($"/places/{place.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rawBody = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("googleapis.com", rawBody);
        Assert.DoesNotContain("key=", rawBody);
        Assert.DoesNotContain("SUPER-SECRET-KEY", rawBody);

        var body = JsonDocument.Parse(rawBody).RootElement;
        var photos = body.GetProperty("photos").EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.Equal(
            new[] { $"{PlacePhotoBaseUrlFixture.PublicBaseUrl}/places/{place.Id}/photos/0" },
            photos);
        Assert.Equal("google", body.GetProperty("photoSource").GetString());
    }

    [Fact]
    public async Task GetPlaces_List_AlsoSynthesizesProxyUrl_NeverStoredGoogleUrl()
    {
        var place = PhotoDtoTestData.MakePlace(
            $"List Google Spot {Guid.NewGuid():N}",
            googlePlaceId: "ChIJ_dto_list",
            photos: [PhotoDtoTestData.StoredGoogleUrlWithKey]);
        var db = _fixture.GetDbContext();
        db.Places.Add(place);
        await db.SaveChangesAsync();

        var client = _fixture.CreateClient();
        var response = await client.GetAsync($"/places?city=Miami&search={Uri.EscapeDataString(place.Name)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rawBody = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("googleapis.com", rawBody);
        Assert.DoesNotContain("key=", rawBody);

        var body = JsonDocument.Parse(rawBody).RootElement;
        var match = body.GetProperty("places").EnumerateArray()
            .First(p => p.GetProperty("id").GetGuid() == place.Id);
        Assert.Equal(
            $"{PlacePhotoBaseUrlFixture.PublicBaseUrl}/places/{place.Id}/photos/0",
            match.GetProperty("photos")[0].GetString());
        Assert.Equal("google", match.GetProperty("photoSource").GetString());
    }

    [Fact]
    public async Task GetPlan_WithStopReferencingGooglePlace_SynthesizesProxyUrlInNestedPlace()
    {
        // Barrido: PlanDetailDto embebe PlaceDto por stop (Features/Plans/PlanDtos.cs).
        // Debe pasar por la misma síntesis, nunca reemitir la URL guardada con key.
        var db = _fixture.GetDbContext();
        var place = PhotoDtoTestData.MakePlace(
            "Plan Stop Google Spot",
            googlePlaceId: "ChIJ_dto_plan",
            photos: [PhotoDtoTestData.StoredGoogleUrlWithKey]);
        var plan = new Plan
        {
            Id = Guid.NewGuid(),
            Name = "Plan With Google Photo Stop",
            City = "Miami",
            Type = "curated",
            IsPublic = true,
        };
        db.Places.Add(place);
        db.Plans.Add(plan);
        db.PlanStops.Add(new PlanStop
        {
            Id = Guid.NewGuid(),
            PlanId = plan.Id,
            PlaceId = place.Id,
            DayNumber = 1,
            OrderIndex = 0,
            TimeBlock = "morning",
        });
        await db.SaveChangesAsync();

        var client = _fixture.CreateClient();
        var response = await client.GetAsync($"/plans/{plan.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rawBody = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("googleapis.com", rawBody);
        Assert.DoesNotContain("key=", rawBody);

        var body = JsonDocument.Parse(rawBody).RootElement;
        var stop = body.GetProperty("days")[0].GetProperty("stops")[0];
        var nestedPlace = stop.GetProperty("place");
        Assert.Equal(
            $"{PlacePhotoBaseUrlFixture.PublicBaseUrl}/places/{place.Id}/photos/0",
            nestedPlace.GetProperty("photos")[0].GetString());
        Assert.Equal("google", nestedPlace.GetProperty("photoSource").GetString());
    }
}

// ── Sin Api:PublicBaseUrl configurada (dev por defecto) -> ruta relativa ──────────────

public class PlaceDtoPhotoSynthesis_DefaultFixtureTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task GetPlace_WithGooglePlaceId_NoPublicBaseUrlConfigured_ReturnsRelativePath()
    {
        var place = PhotoDtoTestData.MakePlace("Relative Google Spot", googlePlaceId: "ChIJ_dto_relative");
        var db = fixture.GetDbContext();
        db.Places.Add(place);
        await db.SaveChangesAsync();

        var client = fixture.CreateClient();
        var response = await client.GetAsync($"/places/{place.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var photos = body.GetProperty("photos").EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.Equal(new[] { $"/places/{place.Id}/photos/0" }, photos);
        Assert.Equal("google", body.GetProperty("photoSource").GetString());
    }

    [Fact]
    public async Task GetPlace_WithoutGooglePlaceId_ExternalPhotos_ReturnsThemDirectly_SourceExternal()
    {
        var external = new List<string>
        {
            "https://images.example-yelp.com/photo-1.jpg",
            "https://cdn.creator-upload.example/photo-2.jpg",
        };
        var place = PhotoDtoTestData.MakePlace("External Photos Spot", googlePlaceId: null, photos: external);
        var db = fixture.GetDbContext();
        db.Places.Add(place);
        await db.SaveChangesAsync();

        var client = fixture.CreateClient();
        var response = await client.GetAsync($"/places/{place.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var photos = body.GetProperty("photos").EnumerateArray().Select(p => p.GetString()!).ToList();
        Assert.Equal(external, photos);
        Assert.Equal("external", body.GetProperty("photoSource").GetString());
    }

    [Fact]
    public async Task GetPlace_NoPhotosNoGooglePlaceId_ReturnsEmptyPhotos_PhotoSourceOmitted()
    {
        var place = PhotoDtoTestData.MakePlace("No Photos Spot", googlePlaceId: null, photos: null);
        var db = fixture.GetDbContext();
        db.Places.Add(place);
        await db.SaveChangesAsync();

        var client = fixture.CreateClient();
        var response = await client.GetAsync($"/places/{place.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("photos").GetArrayLength());
        // DefaultIgnoreCondition = WhenWritingNull (Program.cs): photoSource null se omite.
        Assert.False(body.TryGetProperty("photoSource", out _));
    }

    [Fact]
    public async Task GetPlace_NoGooglePlaceId_FiltersOutDirtyGoogleUrl_DefenseInDepth()
    {
        // Sin GooglePlaceId pero con una URL googleapis.com colada (dato legacy/sucio):
        // el punto único de síntesis la descarta igualmente, nunca debe salir una key.
        var place = PhotoDtoTestData.MakePlace(
            "Dirty No-Google-Id Spot",
            googlePlaceId: null,
            photos: [PhotoDtoTestData.StoredGoogleUrlWithKey]);
        var db = fixture.GetDbContext();
        db.Places.Add(place);
        await db.SaveChangesAsync();

        var client = fixture.CreateClient();
        var response = await client.GetAsync($"/places/{place.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rawBody = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("googleapis.com", rawBody);
        Assert.DoesNotContain("key=", rawBody);

        var body = JsonDocument.Parse(rawBody).RootElement;
        Assert.Equal(0, body.GetProperty("photos").GetArrayLength());
        Assert.False(body.TryGetProperty("photoSource", out _));
    }

    [Fact]
    public async Task GetPlace_NoGooglePlaceId_MixOfExternalAndDirtyGoogleUrl_KeepsOnlyExternal()
    {
        var external = "https://images.example-yelp.com/clean.jpg";
        var place = PhotoDtoTestData.MakePlace(
            "Mixed Photos Spot",
            googlePlaceId: null,
            photos: [external, PhotoDtoTestData.StoredGoogleUrlWithKey]);
        var db = fixture.GetDbContext();
        db.Places.Add(place);
        await db.SaveChangesAsync();

        var client = fixture.CreateClient();
        var response = await client.GetAsync($"/places/{place.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rawBody = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("googleapis.com", rawBody);
        Assert.DoesNotContain("key=", rawBody);

        var body = JsonDocument.Parse(rawBody).RootElement;
        var photos = body.GetProperty("photos").EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.Equal(new[] { external }, photos);
        Assert.Equal("external", body.GetProperty("photoSource").GetString());
    }
}
