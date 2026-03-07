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
    public async Task GetPlaces_DraftAnonymous_Returns401()
    {
        var client = fixture.CreateClient();
        var response = await client.GetAsync("/places?status=draft");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
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
