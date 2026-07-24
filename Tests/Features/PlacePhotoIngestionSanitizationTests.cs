using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.Tests.Features;

/// <summary>
/// T3, "barrido": ninguna ruta de escritura de <c>Place.Photos</c> debe persistir una URL de
/// Google con la API key (ni, por extensión, el preview admin-authed de T3, que requiere auth
/// y 401/403 a un cliente normal). Cubre las rutas que <c>ImportFromUrls</c> NO ejercita:
/// creación directa (<c>POST /admin/places</c>), update (<c>PATCH /admin/places/{id}</c>) y
/// bulk import (<c>POST /admin/places/bulk</c>): todas pasan por
/// <c>PlacePhotoUrls.SanitizeForStorage</c> como defensa en profundidad, por si un curador
/// pega a mano una URL filtrada desde otra respuesta.
/// </summary>
public class PlacePhotoIngestionSanitizationTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    private const string LeakedGoogleUrl =
        "https://places.googleapis.com/v1/places/xyz/photos/abc/media?maxWidthPx=1600&key=SECRET";
    private const string LeakedAdminPreviewUrl =
        "/admin/places/photo-preview?googlePlaceId=ChIJxyz&index=0";
    private const string CleanExternalUrl = "https://photos.example.com/hero.jpg";

    [Fact]
    public async Task CreatePlace_StripsGoogleKeyedUrl_KeepsCleanExternalUrls()
    {
        var client = CreateAdminClient();
        var payload = new
        {
            name = $"Sanitize Create {Guid.NewGuid():N}",
            category = "Food",
            whyThisPlace = "test",
            city = "Miami",
            photos = new[] { LeakedGoogleUrl, CleanExternalUrl }
        };

        var response = await client.PostAsJsonAsync("/admin/places", payload);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var id = body.GetProperty("id").GetGuid();

        var db = fixture.GetDbContext();
        var saved = await db.Places.AsNoTracking().FirstAsync(p => p.Id == id);

        Assert.NotNull(saved.Photos);
        Assert.DoesNotContain(saved.Photos!, url => url.Contains("googleapis.com"));
        Assert.DoesNotContain(saved.Photos!, url => url.Contains("key="));
        Assert.Contains(CleanExternalUrl, saved.Photos!);
        Assert.Single(saved.Photos!);
    }

    [Fact]
    public async Task CreatePlace_OnlyGoogleKeyedUrl_PersistsNullPhotos()
    {
        var client = CreateAdminClient();
        var payload = new
        {
            name = $"Sanitize Create OnlyLeak {Guid.NewGuid():N}",
            category = "Food",
            whyThisPlace = "test",
            city = "Miami",
            photos = new[] { LeakedGoogleUrl, LeakedAdminPreviewUrl }
        };

        var response = await client.PostAsJsonAsync("/admin/places", payload);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var id = body.GetProperty("id").GetGuid();

        var db = fixture.GetDbContext();
        var saved = await db.Places.AsNoTracking().FirstAsync(p => p.Id == id);

        Assert.Null(saved.Photos);
    }

    [Fact]
    public async Task UpdatePlace_StripsGoogleKeyedUrl()
    {
        var db = fixture.GetDbContext();
        var placeId = Guid.NewGuid();
        db.Places.Add(new Place
        {
            Id = placeId,
            Name = "Sanitize Update Target",
            Category = "Food",
            City = "Miami",
            WhyThisPlace = "test",
            Status = "in_review",
        });
        await db.SaveChangesAsync();

        var client = CreateAdminClient();
        var response = await client.PatchAsJsonAsync($"/admin/places/{placeId}",
            new { photos = new[] { LeakedGoogleUrl, LeakedAdminPreviewUrl, CleanExternalUrl } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var freshDb = fixture.GetDbContext();
        var saved = await freshDb.Places.AsNoTracking().FirstAsync(p => p.Id == placeId);

        Assert.NotNull(saved.Photos);
        Assert.Equal(new List<string> { CleanExternalUrl }, saved.Photos);
    }

    [Fact]
    public async Task BulkImport_StripsGoogleKeyedUrlFromManuallyPastedPhotos()
    {
        var client = CreateAdminClient();
        var uniqueName = $"Sanitize Bulk {Guid.NewGuid():N}";
        var payload = new[]
        {
            new
            {
                name = uniqueName,
                category = "Food",
                whyThisPlace = "bulk-sanitize",
                city = "Miami",
                photos = new[] { LeakedGoogleUrl }
            }
        };

        var response = await client.PostAsJsonAsync("/admin/places/bulk", payload);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var db = fixture.GetDbContext();
        var saved = await db.Places.AsNoTracking()
            .FirstAsync(p => p.Name == uniqueName);

        Assert.Null(saved.Photos);
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
