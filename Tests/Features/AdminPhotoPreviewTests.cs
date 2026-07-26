using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.Tests.Features;

/// <summary>
/// Tests de <c>GET /admin/places/photo-preview</c> (T3): el preview de una foto de Google que
/// el admin ve DURANTE el import, ANTES de que el Place exista en DB (el curador todavía no ha
/// guardado el sitio, así que el proxy público de T1, que necesita un Place.Id interno, no
/// aplica). Reusa <c>IPlacePhotoService</c> de T1 (misma <see cref="PhotoProxyFixture"/> /
/// FakePhotos handler) tras <c>[AdminAuthorize]</c>, para que el navegador del admin nunca
/// reciba la URL directa de Google con la key.
/// </summary>
public class AdminPhotoPreviewTests : IClassFixture<PhotoProxyFixture>
{
    private readonly PhotoProxyFixture _fixture;

    public AdminPhotoPreviewTests(PhotoProxyFixture fixture)
    {
        _fixture = fixture;
        _fixture.FakePhotos.Reset();
    }

    [Fact]
    public async Task AdminAuthed_Returns302ToPhotoUri()
    {
        var client = CreateAdminNonRedirectingClient();

        var response = await client.GetAsync(
            "/admin/places/photo-preview?googlePlaceId=ChIJ_preview_happy&index=0");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(FakePhotoHandler.DefaultPhotoUri, response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task KeyGoesInHeaderServerSide_AndNeverAppearsInResponse()
    {
        var client = CreateAdminNonRedirectingClient();

        var response = await client.GetAsync(
            "/admin/places/photo-preview?googlePlaceId=ChIJ_preview_secure&index=0");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        // La key viaja server-side, nunca al navegador del admin.
        Assert.True(_fixture.FakePhotos.KeyHeaderSentWith(PhotoProxyFixture.PhotoKey));

        var location = response.Headers.Location?.ToString() ?? "";
        Assert.DoesNotContain(PhotoProxyFixture.PhotoKey, location);

        var allHeaders = string.Join("\n",
            response.Headers.Concat(response.Content.Headers)
                .Select(h => $"{h.Key}: {string.Join(",", h.Value)}"));
        Assert.DoesNotContain(PhotoProxyFixture.PhotoKey, allHeaders);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(PhotoProxyFixture.PhotoKey, body);
    }

    [Fact]
    public async Task WithoutAuth_Returns401()
    {
        var client = _fixture.CreateNonRedirectingClient();

        var response = await client.GetAsync(
            "/admin/places/photo-preview?googlePlaceId=ChIJ_preview_noauth&index=0");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task NonAdminUser_Returns403()
    {
        // Usuario autenticado por email/password (no Firebase RS256 admin), el mismo patrón
        // que DbRoleAdmin_WithHs256_GetsForbiddenOnAdminEndpoint en AdminPlacesTests.
        var tag = Guid.NewGuid().ToString("N")[..8];
        var email = $"nonadmin-{tag}@test.com";
        var client = _fixture.CreateNonRedirectingClient();

        var registerBody = await (await client.PostAsJsonAsync("/auth/register",
            new { email, password = "TestPass1!" })).Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var accessToken = registerBody.GetProperty("accessToken").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var response = await client.GetAsync(
            "/admin/places/photo-preview?googlePlaceId=ChIJ_preview_forbidden&index=0");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task NoPhotosAtIndex_Returns404_NeverGoogleErrorLeaked()
    {
        _fixture.FakePhotos.ReturnNoPhotos = true;
        var client = CreateAdminNonRedirectingClient();

        var response = await client.GetAsync(
            "/admin/places/photo-preview?googlePlaceId=ChIJ_preview_nophotos&index=0");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task MissingGooglePlaceId_Returns400()
    {
        var client = CreateAdminNonRedirectingClient();

        var response = await client.GetAsync("/admin/places/photo-preview?index=0");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Response_SetsNoStore()
    {
        var client = CreateAdminNonRedirectingClient();

        var response = await client.GetAsync(
            "/admin/places/photo-preview?googlePlaceId=ChIJ_preview_nocache&index=0");

        Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? "");
    }

    private HttpClient CreateAdminNonRedirectingClient()
    {
        var adminEmail = $"admin-{Guid.NewGuid():N}@locallist.ai";
        var adminFbUid = $"fb-admin-{Guid.NewGuid():N}";

        var db = _fixture.GetDbContext();
        db.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Email = adminEmail,
            FirebaseUid = adminFbUid,
            Role = "admin"
        });
        db.SaveChanges();

        var client = _fixture.CreateNonRedirectingClient();
        var token = _fixture.CreateToken(adminFbUid, adminEmail);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
