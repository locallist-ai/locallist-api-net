using System.Text.Json;
using LocalList.API.NET.Features.Favorites;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.Tests.Features;

/// <summary>
/// Favoritos de sitios (F-BE) sobre DB real (ApiFixture = Testcontainers PostgreSQL):
///
///   (a) favoritar → 200 + fila; re-favoritar → idempotente sin duplicar.
///   (b) DELETE idempotente (204 aunque no existiera la fila).
///   (c) GET paginado con ORDEN TOTAL (created_at DESC, tiebreaker place_id DESC) — se asertan
///       secuencias exactas cruzando frontera de página; el tiebreaker se prueba con timestamps
///       iguales. Se siembra CON SaveChanges POR FILA para no dejar el test vacuo.
///   (d) cap: free con 50 → 403 favorites_limit_reached; pro con 50+ → OK (ilimitado).
///   (e) carrera: N favoritos concurrentes partiendo de 49 → count final EXACTO 50, excedentes 403.
///   (f) 401 anónimo (PUT y GET).
///   (g) place inexistente / no publicado → 404 opaco.
///   (h) cascade: DELETE /account borra los favoritos (GDPR) y no se rompe; borrar el place también.
///   (i) fotos: un place Google favoritado sale con la URL del proxy sintetizada, nunca la key.
///
/// El cap se lee con el tier FRESCO de DB (no del claim JWT). El tier por defecto del helper
/// <c>CreateAppAuthenticatedClientWithUser</c> es "free".
/// </summary>
public class FavoritesTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    // ── (a) favoritar + idempotencia ─────────────────────────────────────────

    [Fact]
    public async Task Put_FavoritesPlace_Returns200_AndPersistsRow()
    {
        var (client, userId) = await AuthedUser("fav-add");
        var place = await SeedPlace();

        var res = await client.PutAsync($"/favorites/{place.Id}", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("favorited").GetBoolean());

        Assert.Equal(1, await FavoriteCount(userId));
    }

    [Fact]
    public async Task Put_SamePlaceTwice_Idempotent_NoDuplicate()
    {
        var (client, userId) = await AuthedUser("fav-idem");
        var place = await SeedPlace();

        var first = await client.PutAsync($"/favorites/{place.Id}", null);
        var second = await client.PutAsync($"/favorites/{place.Id}", null);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(1, await FavoriteCount(userId));
    }

    // ── (b) DELETE idempotente ───────────────────────────────────────────────

    [Fact]
    public async Task Delete_ExistingFavorite_Returns204_AndRemovesRow()
    {
        var (client, userId) = await AuthedUser("fav-del");
        var place = await SeedPlace();
        await client.PutAsync($"/favorites/{place.Id}", null);

        var res = await client.DeleteAsync($"/favorites/{place.Id}");
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        Assert.Equal(0, await FavoriteCount(userId));
    }

    [Fact]
    public async Task Delete_NonExistentFavorite_Returns204_Idempotent()
    {
        var (client, _) = await AuthedUser("fav-del-noop");
        var place = await SeedPlace();

        var res = await client.DeleteAsync($"/favorites/{place.Id}");
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
    }

    // ── (c) GET paginado con orden total ─────────────────────────────────────

    [Fact]
    public async Task Get_Paginated_OrderedByCreatedAtDesc_TotalOrderAcrossPageBoundary()
    {
        var (client, userId) = await AuthedUser("fav-list");

        // 8 places favoritados con created_at estrictamente creciente (base + i min): el más
        // reciente (i=7) debe salir primero. SaveChanges POR FILA para que el orden no sea vacuo.
        var baseTime = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var placeIds = new List<Guid>();
        for (var i = 0; i < 8; i++)
        {
            var place = await SeedPlace(name: $"List {i}");
            placeIds.Add(place.Id);
            var db = fixture.GetDbContext();
            db.Favorites.Add(new Favorite { UserId = userId, PlaceId = place.Id, CreatedAt = baseTime.AddMinutes(i) });
            await db.SaveChangesAsync();
        }

        // Orden esperado descendente por recencia: i = 7,6,5,4,3,2,1,0.
        var expected = Enumerable.Range(0, 8).Reverse().Select(i => placeIds[i]).ToList();

        // Página 1 (limit 5, offset 0) + página 2 (offset 5) — concatenadas deben dar el orden
        // total sin duplicar ni omitir en la frontera.
        var page1 = await GetFavoriteIds(client, "?limit=5&offset=0");
        var page2 = await GetFavoriteIds(client, "?limit=5&offset=5");

        Assert.Equal(expected.Take(5), page1);
        Assert.Equal(expected.Skip(5), page2);
        Assert.Equal(expected, page1.Concat(page2).ToList());
    }

    [Fact]
    public async Task Get_EqualCreatedAt_TiebreaksByPlaceIdDesc()
    {
        var (client, userId) = await AuthedUser("fav-tie");
        var sameTime = new DateTimeOffset(2026, 2, 2, 8, 0, 0, TimeSpan.Zero);

        var pA = await SeedPlace(name: "Tie A");
        var pB = await SeedPlace(name: "Tie B");
        var db = fixture.GetDbContext();
        db.Favorites.Add(new Favorite { UserId = userId, PlaceId = pA.Id, CreatedAt = sameTime });
        await db.SaveChangesAsync();
        db.Favorites.Add(new Favorite { UserId = userId, PlaceId = pB.Id, CreatedAt = sameTime });
        await db.SaveChangesAsync();

        var ids = await GetFavoriteIds(client, "");

        // created_at igual → desempata place_id DESC. OJO: el orden uuid de Postgres es
        // lexicográfico sobre el hex canónico (byte a byte), que NO coincide con el
        // Guid.CompareTo de .NET; por eso el esperado se ordena por la representación string
        // ordinal (equivalente al ORDER BY id DESC de Postgres), no por Guid nativo.
        var expected = new[] { pA.Id, pB.Id }
            .OrderByDescending(g => g.ToString(), StringComparer.Ordinal)
            .ToList();
        Assert.Equal(expected, ids);
    }

    // ── (d) cap 50 free / ilimitado pro ──────────────────────────────────────

    [Fact]
    public async Task Put_FreeUser_At50_Returns403_FavoritesLimitReached()
    {
        var (client, userId) = await AuthedUser("fav-cap-free");
        await SeedFavorites(userId, 50);

        var extra = await SeedPlace();
        var res = await client.PutAsync($"/favorites/{extra.Id}", null);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("favorites_limit_reached", body.GetProperty("error").GetString());
        Assert.Equal(50, body.GetProperty("used").GetInt32());
        Assert.Equal(FavoritesController.FreeFavoritesLimit, body.GetProperty("limit").GetInt32());

        // El 51º no coló: sigue exactamente en 50.
        Assert.Equal(50, await FavoriteCount(userId));
    }

    [Fact]
    public async Task Put_ProUser_BeyondCap_Ok_Unlimited()
    {
        var userId = Guid.NewGuid();
        var client = await fixture.CreateAppAuthenticatedClientWithUser(
            userId, $"fav-pro-{userId:N}@test.com", tier: "pro");
        await SeedFavorites(userId, 55);

        var extra = await SeedPlace();
        var res = await client.PutAsync($"/favorites/{extra.Id}", null);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(56, await FavoriteCount(userId));
    }

    // ── (e) carrera: no overshoot ────────────────────────────────────────────

    [Fact]
    public async Task Put_ConcurrentFromFortyNine_ExactlyOneSucceeds_CountEqualsFifty()
    {
        var (client, userId) = await AuthedUser("fav-race");
        await SeedFavorites(userId, 49); // un único hueco antes del cap de 50

        // 10 places DISTINTOS favoritados a la vez: solo 1 debe entrar (llega a 50), 9 → 403.
        var places = new List<Guid>();
        for (var i = 0; i < 10; i++) places.Add((await SeedPlace(name: $"Race {i}")).Id);

        var responses = await Task.WhenAll(places.Select(id => client.PutAsync($"/favorites/{id}", null)));

        var ok = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        var denied = responses.Count(r => r.StatusCode == HttpStatusCode.Forbidden);

        Assert.Equal(1, ok);
        Assert.Equal(9, denied);
        // Sin overshoot: exactamente 50, jamás 51.
        Assert.Equal(50, await FavoriteCount(userId));

        foreach (var d in responses.Where(r => r.StatusCode == HttpStatusCode.Forbidden))
        {
            var body = await d.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("favorites_limit_reached", body.GetProperty("error").GetString());
        }
    }

    // ── (f) 401 anónimo ──────────────────────────────────────────────────────

    [Fact]
    public async Task Put_Anonymous_Returns401()
    {
        var client = fixture.CreateClient();
        var res = await client.PutAsync($"/favorites/{Guid.NewGuid()}", null);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Get_Anonymous_Returns401()
    {
        var client = fixture.CreateClient();
        var res = await client.GetAsync("/favorites");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    // ── (g) place inexistente / no publicado → 404 opaco ─────────────────────

    [Fact]
    public async Task Put_NonExistentPlace_Returns404()
    {
        var (client, _) = await AuthedUser("fav-404");
        var res = await client.PutAsync($"/favorites/{Guid.NewGuid()}", null);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Put_UnpublishedPlace_Returns404_Opaque()
    {
        var (client, userId) = await AuthedUser("fav-draft");
        var draft = await SeedPlace(status: "draft");

        var res = await client.PutAsync($"/favorites/{draft.Id}", null);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal(0, await FavoriteCount(userId));
    }

    // ── (h) cascade GDPR + place ─────────────────────────────────────────────

    [Fact]
    public async Task DeleteAccount_CascadesFavorites_AndDoesNotBreak()
    {
        var (client, userId) = await AuthedUser("fav-gdpr");
        var place = await SeedPlace();
        await client.PutAsync($"/favorites/{place.Id}", null);
        Assert.Equal(1, await FavoriteCount(userId));

        var res = await client.DeleteAsync("/account");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        Assert.Equal(0, await FavoriteCount(userId));
    }

    [Fact]
    public async Task DeletePlace_CascadesFavorites()
    {
        var (client, userId) = await AuthedUser("fav-place-cascade");
        var place = await SeedPlace();
        await client.PutAsync($"/favorites/{place.Id}", null);
        Assert.Equal(1, await FavoriteCount(userId));

        var db = fixture.GetDbContext();
        await db.Places.Where(p => p.Id == place.Id).ExecuteDeleteAsync();

        Assert.Equal(0, await FavoriteCount(userId));
    }

    // ── (i) fotos: proxy sintetizado, nunca la key ───────────────────────────

    [Fact]
    public async Task Get_GoogleSourcedFavorite_SynthesizesProxyUrl_NeverKey()
    {
        var (client, userId) = await AuthedUser("fav-photo");
        var place = await SeedPlace(name: "Google Spot", googlePlaceId: "gpid-fav-photo");
        // Foto guardada con la key de Google (simula dato ingerido) — NUNCA debe reemitirse.
        var db = fixture.GetDbContext();
        var tracked = await db.Places.FirstAsync(p => p.Id == place.Id);
        tracked.Photos = new List<string> { "https://places.googleapis.com/v1/PHOTO?key=SECRET_KEY" };
        await db.SaveChangesAsync();

        await client.PutAsync($"/favorites/{place.Id}", null);

        var res = await client.GetAsync("/favorites");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var placeDto = body.GetProperty("places").EnumerateArray().Single();

        var photos = placeDto.GetProperty("photos").EnumerateArray().Select(x => x.GetString()!).ToList();
        Assert.Single(photos);
        Assert.Equal($"/places/{place.Id}/photos/0", photos[0]);
        Assert.DoesNotContain(photos, url => url.Contains("googleapis", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(photos, url => url.Contains("SECRET_KEY", StringComparison.Ordinal));
        Assert.Equal("google", placeDto.GetProperty("photoSource").GetString());
    }

    // ── /favorites/ids ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetIds_ReturnsFavoritePlaceIds()
    {
        var (client, userId) = await AuthedUser("fav-ids");
        var p1 = await SeedPlace();
        var p2 = await SeedPlace();
        await client.PutAsync($"/favorites/{p1.Id}", null);
        await client.PutAsync($"/favorites/{p2.Id}", null);

        var res = await client.GetAsync("/favorites/ids");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var ids = body.GetProperty("ids").EnumerateArray().Select(x => x.GetGuid()).ToHashSet();

        Assert.Contains(p1.Id, ids);
        Assert.Contains(p2.Id, ids);
        Assert.Equal(2, ids.Count);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<(HttpClient client, Guid userId)> AuthedUser(string tag)
    {
        var userId = Guid.NewGuid();
        var client = await fixture.CreateAppAuthenticatedClientWithUser(userId, $"{tag}-{userId:N}@test.com");
        return (client, userId);
    }

    private async Task<Place> SeedPlace(
        string name = "Fav Spot", string status = "published", string? googlePlaceId = null)
    {
        var db = fixture.GetDbContext();
        var place = new Place
        {
            Id = Guid.NewGuid(),
            Name = name,
            Category = "food",
            City = "Miami",
            WhyThisPlace = "Seeded for favorites tests",
            Status = status,
            GooglePlaceId = googlePlaceId,
            Source = googlePlaceId != null ? "google" : "curated",
        };
        db.Places.Add(place);
        await db.SaveChangesAsync();
        return place;
    }

    /// <summary>Siembra <paramref name="count"/> favoritos directos (bypasa el endpoint) para el user.</summary>
    private async Task SeedFavorites(Guid userId, int count)
    {
        var db = fixture.GetDbContext();
        var baseTime = new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < count; i++)
        {
            var place = new Place
            {
                Id = Guid.NewGuid(),
                Name = $"Seed Fav {i}",
                Category = "food",
                City = "Miami",
                WhyThisPlace = "seed",
                Status = "published",
            };
            db.Places.Add(place);
            db.Favorites.Add(new Favorite { UserId = userId, PlaceId = place.Id, CreatedAt = baseTime.AddSeconds(i) });
        }
        await db.SaveChangesAsync();
    }

    private async Task<int> FavoriteCount(Guid userId) =>
        await fixture.GetDbContext().Favorites.CountAsync(f => f.UserId == userId);

    private static async Task<List<Guid>> GetFavoriteIds(HttpClient client, string queryString)
    {
        var res = await client.GetAsync($"/favorites{queryString}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("places").EnumerateArray()
            .Select(p => p.GetProperty("id").GetGuid())
            .ToList();
    }
}
