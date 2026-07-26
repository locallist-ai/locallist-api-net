using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using LocalList.API.NET.Features.Admin.Places;
using LocalList.API.Tests.Unit.Llm;

namespace LocalList.API.Tests.Unit;

/// <summary>
/// T3: verifica que la lógica REAL de <see cref="GooglePlacesService"/> (no la fake usada por
/// los tests de integración de AdminPlacesController) ya no construye una URL
/// <c>places.googleapis.com</c> con la API key en el query string para ningún caller
/// (SearchAsync -> GooglePlacePreview.Photos, GetDetailsAsync -> GooglePlaceDetails.Photos).
/// En su lugar debe referenciar el preview admin-authed de T3
/// (<see cref="AdminPlacePhotoPreviewUrls"/>).
///
/// Los tests de integración (AdminPlacesTests, etc.) sustituyen IGooglePlacesService por un
/// fake en DI y por tanto NUNCA ejercitan este código: de ahí que este unit test con un
/// HttpMessageHandler falso sea el único punto de cobertura de este leak concreto.
/// </summary>
public class GooglePlacesServicePhotoResolutionTests
{
    private const string ApiKey = "SECRET_INGEST_KEY";

    private static GooglePlacesService CreateService(string responseBody, string? publicBaseUrl = null)
    {
        var handler = new CapturingHandler(System.Net.HttpStatusCode.OK, responseBody);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://places.googleapis.com") };

        var configValues = new Dictionary<string, string?> { ["GooglePlaces:ApiKey"] = ApiKey };
        if (publicBaseUrl != null) configValues["Api:PublicBaseUrl"] = publicBaseUrl;

        var config = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();
        return new GooglePlacesService(http, config, NullLogger<GooglePlacesService>.Instance);
    }

    [Fact]
    public async Task SearchAsync_NeverEmitsGoogleUrlWithKey_ReferencesAdminPreviewInstead()
    {
        const string placeId = "ChIJsearch123";
        const string body = """
        {
          "places": [
            {
              "id": "ChIJsearch123",
              "displayName": { "text": "Test Ramen" },
              "photos": [
                { "name": "places/ChIJsearch123/photos/PHOTO_A" },
                { "name": "places/ChIJsearch123/photos/PHOTO_B" }
              ]
            }
          ]
        }
        """;

        var service = CreateService(body);
        var results = await service.SearchAsync("ramen", CancellationToken.None);

        Assert.NotNull(results);
        var preview = Assert.Single(results!);
        Assert.Equal(2, preview.Photos.Count);

        foreach (var photoUrl in preview.Photos)
        {
            Assert.DoesNotContain("googleapis.com", photoUrl);
            Assert.DoesNotContain(ApiKey, photoUrl);
            Assert.DoesNotContain("key=", photoUrl);
            Assert.Contains($"/admin/places/photo-preview?googlePlaceId={placeId}", photoUrl);
        }

        Assert.Equal($"/admin/places/photo-preview?googlePlaceId={placeId}&index=0", preview.Photos[0]);
        Assert.Equal($"/admin/places/photo-preview?googlePlaceId={placeId}&index=1", preview.Photos[1]);
    }

    [Fact]
    public async Task GetDetailsAsync_NeverEmitsGoogleUrlWithKey_ReferencesAdminPreviewInstead()
    {
        const string placeId = "ChIJdetails456";
        const string body = """
        {
          "id": "ChIJdetails456",
          "displayName": { "text": "Test Place" },
          "photos": [
            { "name": "places/ChIJdetails456/photos/PHOTO_A" }
          ]
        }
        """;

        var service = CreateService(body);
        var details = await service.GetDetailsAsync(placeId, CancellationToken.None);

        Assert.NotNull(details);
        var photoUrl = Assert.Single(details!.Photos);
        Assert.DoesNotContain("googleapis.com", photoUrl);
        Assert.DoesNotContain(ApiKey, photoUrl);
        Assert.DoesNotContain("key=", photoUrl);
        Assert.Equal($"/admin/places/photo-preview?googlePlaceId={placeId}&index=0", photoUrl);
    }

    [Fact]
    public async Task GetDetailsAsync_WithPublicBaseUrlConfigured_ReturnsAbsolutePreviewUrl()
    {
        const string placeId = "ChIJabs789";
        const string body = """
        {
          "id": "ChIJabs789",
          "displayName": { "text": "Absolute URL Place" },
          "photos": [ { "name": "places/ChIJabs789/photos/PHOTO_A" } ]
        }
        """;

        var service = CreateService(body, publicBaseUrl: "https://api.locallist.ai");
        var details = await service.GetDetailsAsync(placeId, CancellationToken.None);

        Assert.NotNull(details);
        var photoUrl = Assert.Single(details!.Photos);
        Assert.Equal(
            $"https://api.locallist.ai/admin/places/photo-preview?googlePlaceId={placeId}&index=0",
            photoUrl);
    }

    [Fact]
    public async Task GetDetailsAsync_NoPhotos_ReturnsEmptyList()
    {
        const string body = """
        { "id": "ChIJnophotos", "displayName": { "text": "No Photos Place" }, "photos": [] }
        """;

        var service = CreateService(body);
        var details = await service.GetDetailsAsync("ChIJnophotos", CancellationToken.None);

        Assert.NotNull(details);
        Assert.Empty(details!.Photos);
    }
}
