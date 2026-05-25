using System.Net.Http.Headers;
using System.Text.Json;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.Tests.Features;

public class PlacesTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task GetPlaces_ReturnsPublishedOnly()
    {
        var db = fixture.GetDbContext();
        db.Places.Add(MakePlace("Pub Cafe", status: "published"));
        db.Places.Add(MakePlace("Draft Cafe", status: "draft"));
        await db.SaveChangesAsync();

        var client = fixture.CreateClient();
        var response = await client.GetAsync("/places");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        foreach (var p in body.GetProperty("places").EnumerateArray())
            Assert.Equal("published", p.GetProperty("status").GetString());
    }

    [Fact]
    public async Task GetPlaces_FiltersByCity()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var db = fixture.GetDbContext();
        db.Places.Add(MakePlace($"NYC {tag}", city: $"NYC-{tag}"));
        db.Places.Add(MakePlace($"MIA {tag}", city: $"MIA-{tag}"));
        await db.SaveChangesAsync();

        var client = fixture.CreateClient();
        var response = await client.GetAsync($"/places?city=NYC-{tag}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var places = body.GetProperty("places");
        Assert.True(places.GetArrayLength() >= 1);
        foreach (var p in places.EnumerateArray())
            Assert.Equal($"NYC-{tag}", p.GetProperty("city").GetString());
    }

    [Fact]
    public async Task GetPlaces_FiltersByCategory()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var db = fixture.GetDbContext();
        db.Places.Add(MakePlace($"Food {tag}", category: $"Food-{tag}"));
        db.Places.Add(MakePlace($"Coffee {tag}", category: $"Coffee-{tag}"));
        await db.SaveChangesAsync();

        var client = fixture.CreateClient();
        var response = await client.GetAsync($"/places?category=Coffee-{tag}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        foreach (var p in body.GetProperty("places").EnumerateArray())
            Assert.Equal($"Coffee-{tag}", p.GetProperty("category").GetString());
    }

    [Fact]
    public async Task GetPlaces_NonAdmin_StatusDraftIgnored()
    {
        // Seed: 1 published + 1 draft, ambos con un tag único para aislar este test
        // del resto de rows que otros tests comparten en la misma BD.
        var tag = Guid.NewGuid().ToString("N")[..8];
        var db = fixture.GetDbContext();
        db.Places.Add(MakePlace($"Pub {tag}", status: "published", city: $"CITY-{tag}"));
        db.Places.Add(MakePlace($"Draft {tag}", status: "draft", city: $"CITY-{tag}"));
        await db.SaveChangesAsync();

        // Usuario autenticado NORMAL (dominio no-admin). Usamos el flujo HS256 de la
        // app (AppToken), idéntico al que emite JwtTokenService para usuarios reales.
        var userId = Guid.NewGuid();
        var userEmail = $"user-{tag}@test.com";
        var client = fixture.CreateClient();
        var token = fixture.CreateAppToken(userId, userEmail);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync($"/places?status=draft&city=CITY-{tag}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var places = body.GetProperty("places");
        Assert.Equal(1, places.GetArrayLength());
        Assert.Equal("published", places[0].GetProperty("status").GetString());
        Assert.Equal($"Pub {tag}", places[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task GetPlaces_Admin_StatusDraftRespected()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var db = fixture.GetDbContext();
        db.Places.Add(MakePlace($"Pub {tag}", status: "published", city: $"CITY-{tag}"));
        db.Places.Add(MakePlace($"Draft {tag}", status: "draft", city: $"CITY-{tag}"));
        await db.SaveChangesAsync();

        // Admin = email bajo @locallist.ai (igual que AdminAuthorizationFilter).
        // Usamos el mismo patrón que AdminPlacesTests: token Firebase RS256.
        var adminEmail = $"admin-{tag}@locallist.ai";
        var adminFbUid = $"fb-admin-{tag}";
        var client = fixture.CreateClient();
        var token = fixture.CreateToken(adminFbUid, adminEmail);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync($"/places?status=draft&city=CITY-{tag}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var places = body.GetProperty("places");
        Assert.Equal(1, places.GetArrayLength());
        Assert.Equal("draft", places[0].GetProperty("status").GetString());
        Assert.Equal($"Draft {tag}", places[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task GetPlaces_Anonymous_OnlyPublished()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var db = fixture.GetDbContext();
        db.Places.Add(MakePlace($"Pub {tag}", status: "published", city: $"CITY-{tag}"));
        db.Places.Add(MakePlace($"Draft {tag}", status: "draft", city: $"CITY-{tag}"));
        await db.SaveChangesAsync();

        // Sin header Authorization → caller anónimo. Debe recibir 200 con solo published,
        // ignorando silenciosamente el ?status=draft.
        var client = fixture.CreateClient();
        var response = await client.GetAsync($"/places?status=draft&city=CITY-{tag}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var places = body.GetProperty("places");
        Assert.Equal(1, places.GetArrayLength());
        Assert.Equal("published", places[0].GetProperty("status").GetString());
        Assert.Equal($"Pub {tag}", places[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task GetPlaces_AppHs256WithAdminEmail_DoesNotSeeDrafts()
    {
        // Defense-in-depth: an HS256 app token whose email happens to be @locallist.ai
        // (e.g. a legacy user seeded directly in DB) must NOT see draft/in_review places.
        // IsAdminCaller requires Firebase RS256 issuer — HS256 is always rejected.
        var tag = Guid.NewGuid().ToString("N")[..8];
        var db = fixture.GetDbContext();
        db.Places.Add(MakePlace($"Pub {tag}", status: "published", city: $"CITY-{tag}"));
        db.Places.Add(MakePlace($"Draft {tag}", status: "draft", city: $"CITY-{tag}"));

        // Seed user with admin-domain email directly (bypasses /auth/register 403 block)
        var userId = Guid.NewGuid();
        db.Users.Add(new User { Id = userId, Email = $"hs256-admin-{tag}@locallist.ai" });
        await db.SaveChangesAsync();

        var client = fixture.CreateClient();
        var appToken = fixture.CreateAppToken(userId, $"hs256-admin-{tag}@locallist.ai");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", appToken);

        var response = await client.GetAsync($"/places?status=draft&city=CITY-{tag}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var places = body.GetProperty("places");
        Assert.Equal(1, places.GetArrayLength());
        Assert.Equal("published", places[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task GetPlace_ReturnsDetail()
    {
        var place = MakePlace("Detail Spot");
        var db = fixture.GetDbContext();
        db.Places.Add(place);
        await db.SaveChangesAsync();

        var client = fixture.CreateClient();
        var response = await client.GetAsync($"/places/{place.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Detail Spot", body.GetProperty("name").GetString());
    }

    [Fact]
    public async Task GetPlaces_PublicSearchWithWildcards_EscapesAsLiteral()
    {
        var db = fixture.GetDbContext();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        db.Places.AddRange(
            new Place { Id = Guid.NewGuid(), Name = $"100% Ramen {suffix}", Category = "Food", City = "Miami", WhyThisPlace = "test", Status = "published" },
            new Place { Id = Guid.NewGuid(), Name = $"Pure Ramen {suffix}", Category = "Food", City = "Miami", WhyThisPlace = "test", Status = "published" }
        );
        await db.SaveChangesAsync();

        // Anonymous call — no auth header.
        var client = fixture.CreateClient();
        var res = await client.GetAsync($"/places?search=100%25+Ramen+{suffix}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        // "%" treated as literal: only "100% Ramen …" matches, not both.
        Assert.Equal(1, body.GetProperty("total").GetInt32());
        Assert.Equal($"100% Ramen {suffix}", body.GetProperty("places")[0].GetProperty("name").GetString());
    }

    private static Place MakePlace(
        string name,
        string status = "published",
        string city = "Miami",
        string category = "Food") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Category = category,
        City = city,
        WhyThisPlace = "Great spot",
        Status = status
    };
}
