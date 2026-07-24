using LocalList.API.NET.Shared.Dtos;
using LocalList.API.NET.Features.Builder.Services;
using LocalList.API.NET.Shared.Data.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalList.API.Tests.Unit;

/// <summary>
/// T2 (photo-proxy): <c>SchedulingService.ResolveStopPlaces</c> alimenta directamente las
/// respuestas de <c>/builder/chat</c> y <c>/chat/generate</c> (vía <c>ResolvedPlaceDto</c>,
/// no <c>PlaceDto</c>): un consumidor de fotos de Place aparte del DTO público. Antes de
/// este fix reemitía <c>place.Photos</c> tal cual, filtrando la URL <c>places.googleapis.com</c>
/// con key al cliente durante la generación de planes. Verifica que pasa por la misma
/// síntesis que <see cref="PlaceDto"/> (vía <see cref="PlacePhotoUrls"/>).
/// </summary>
public class SchedulingServicePhotoSynthesisTests
{
    private const string StoredGoogleUrlWithKey =
        "https://places.googleapis.com/v1/places/ABC/photos/XYZ/media?maxWidthPx=1600&key=SUPER-SECRET-KEY";

    private static SchedulingService Svc(string? publicBaseUrl = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(publicBaseUrl is null
                ? []
                : new Dictionary<string, string?> { ["Api:PublicBaseUrl"] = publicBaseUrl })
            .Build();
        return new SchedulingService(NullLogger<SchedulingService>.Instance, config: config);
    }

    private static Place MakePlace(string? googlePlaceId, List<string>? photos) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Test Place",
        Category = "Food",
        City = "Miami",
        Status = "published",
        WhyThisPlace = "test",
        GooglePlaceId = googlePlaceId,
        Photos = photos,
    };

    private static ScheduledStopDto Stop(Guid placeId) => new()
    {
        PlaceId = placeId,
        DayNumber = 1,
        OrderIndex = 0,
        TimeBlock = "morning",
        SuggestedDurationMin = 60,
    };

    [Fact]
    public void PlaceWithGoogleId_SynthesizesAbsoluteProxyUrl_NeverStoredKeyedUrl()
    {
        var place = MakePlace(googlePlaceId: "ChIJ_sched_abs", photos: [StoredGoogleUrlWithKey]);
        var svc = Svc(publicBaseUrl: "https://api.test.locallist.ai");

        var result = svc.ResolveStopPlaces([Stop(place.Id)], [place]).Single();

        Assert.NotNull(result.Place);
        Assert.Equal(
            new List<string> { $"https://api.test.locallist.ai/places/{place.Id}/photos/0" },
            result.Place!.Photos);
        Assert.Equal("google", result.Place.PhotoSource);
        Assert.DoesNotContain(result.Place.Photos!, url => url.Contains("googleapis.com"));
        Assert.DoesNotContain(result.Place.Photos!, url => url.Contains("key="));
    }

    [Fact]
    public void PlaceWithGoogleId_NoPublicBaseUrl_SynthesizesRelativeProxyPath()
    {
        var place = MakePlace(googlePlaceId: "ChIJ_sched_rel", photos: null);
        var svc = Svc(publicBaseUrl: null);

        var result = svc.ResolveStopPlaces([Stop(place.Id)], [place]).Single();

        Assert.Equal(
            new List<string> { $"/places/{place.Id}/photos/0" },
            result.Place!.Photos);
        Assert.Equal("google", result.Place.PhotoSource);
    }

    [Fact]
    public void PlaceWithoutGoogleId_ExternalPhotos_PassThroughDirectly_SourceExternal()
    {
        var external = new List<string> { "https://images.example-yelp.com/photo.jpg" };
        var place = MakePlace(googlePlaceId: null, photos: external);
        var svc = Svc();

        var result = svc.ResolveStopPlaces([Stop(place.Id)], [place]).Single();

        Assert.Equal(external, result.Place!.Photos);
        Assert.Equal("external", result.Place.PhotoSource);
    }

    [Fact]
    public void PlaceWithoutGoogleId_NoPhotos_EmptyPhotos_SourceNull()
    {
        var place = MakePlace(googlePlaceId: null, photos: null);
        var svc = Svc();

        var result = svc.ResolveStopPlaces([Stop(place.Id)], [place]).Single();

        Assert.Empty(result.Place!.Photos!);
        Assert.Null(result.Place.PhotoSource);
    }

    [Fact]
    public void ConfigLessConstructor_StillDegradesSafely_NeverThrows()
    {
        // SchedulingServiceScheduleTests construye el servicio SIN config (config: null por
        // defecto). Comprobamos que ResolveStopPlaces sigue funcionando (fallback relativo)
        // en vez de lanzar NullReferenceException.
        var place = MakePlace(googlePlaceId: "ChIJ_sched_noconfig", photos: null);
        var svc = new SchedulingService(NullLogger<SchedulingService>.Instance);

        var result = svc.ResolveStopPlaces([Stop(place.Id)], [place]).Single();

        Assert.Equal($"/places/{place.Id}/photos/0", result.Place!.Photos!.Single());
        Assert.Equal("google", result.Place.PhotoSource);
    }
}
