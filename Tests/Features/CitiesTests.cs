using System.Net;
using System.Net.Http.Json;
using LocalList.API.NET.Features.Cities;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.Tests.Features;

/// <summary>
/// Tests del registry de ciudades (Pablo 2026-04-27 — custom builder permite
/// añadir ciudades nuevas).
/// </summary>
public class CitiesTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    [Fact]
    public async Task NormalizeName_StripsAccentsAndLowercases()
    {
        Assert.Equal("malaga", CitiesController.NormalizeName("Málaga"));
        Assert.Equal("sao paulo", CitiesController.NormalizeName("São Paulo"));
        Assert.Equal("miami", CitiesController.NormalizeName("MIAMI"));
        Assert.Equal("miami", CitiesController.NormalizeName("  Miami  "));
    }

    [Fact]
    public async Task Search_PrefixMatch_ReturnsCities()
    {
        var db = fixture.GetDbContext();
        var name = $"Miami_{Guid.NewGuid():N}";
        db.Cities.Add(new City
        {
            Id = Guid.NewGuid(),
            Name = name,
            NormalizedName = CitiesController.NormalizeName(name),
            Source = "seed",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var client = fixture.CreateClient();
        var response = await client.GetAsync($"/cities/search?q={name.Substring(0, 3)}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<SearchResponse>();
        Assert.NotNull(body);
        Assert.Contains(body!.Cities, c => c.Name == name);
    }

    [Fact]
    public async Task PostCity_NewName_CreatesAndReturnsCity()
    {
        var (userId, fbUid) = await CreateUser("city-create");
        var client = fixture.CreateAuthenticatedClient(userId, fbUid);
        var name = $"NewCity_{Guid.NewGuid():N}";

        var response = await client.PostAsJsonAsync("/cities", new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<CityDto>();
        Assert.NotNull(body);
        Assert.Equal(name, body!.Name);
        Assert.Equal("user", body.Source);
        Assert.NotEqual(Guid.Empty, body.Id);

        // Verifica persistencia con normalized lowercase.
        var after = fixture.GetDbContext();
        var stored = await after.Cities.FindAsync(body.Id);
        Assert.NotNull(stored);
        Assert.Equal(CitiesController.NormalizeName(name), stored!.NormalizedName);
    }

    [Fact]
    public async Task PostCity_DuplicateName_ReturnsExisting()
    {
        var (userId, fbUid) = await CreateUser("city-dup");
        var client = fixture.CreateAuthenticatedClient(userId, fbUid);
        var name = $"DupCity_{Guid.NewGuid():N}";

        // Primer POST crea
        var first = await client.PostAsJsonAsync("/cities", new { name });
        var firstBody = await first.Content.ReadFromJsonAsync<CityDto>();
        Assert.NotNull(firstBody);

        // Segundo POST con casing/accents distintos devuelve el mismo
        var second = await client.PostAsJsonAsync("/cities", new { name = name.ToUpperInvariant() });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondBody = await second.Content.ReadFromJsonAsync<CityDto>();
        Assert.NotNull(secondBody);
        Assert.Equal(firstBody!.Id, secondBody!.Id);
    }

    [Fact]
    public async Task PostCity_InvalidName_Returns400()
    {
        var (userId, fbUid) = await CreateUser("city-invalid");
        var client = fixture.CreateAuthenticatedClient(userId, fbUid);

        var tooShort = await client.PostAsJsonAsync("/cities", new { name = "M" });
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);

        var empty = await client.PostAsJsonAsync("/cities", new { name = "  " });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    }

    private class SearchResponse
    {
        public List<CityDto> Cities { get; set; } = new();
    }

    private async Task<(Guid userId, string firebaseUid)> CreateUser(string prefix)
    {
        var userId = Guid.NewGuid();
        var firebaseUid = $"fb-{prefix}-{userId:N}";
        var db = fixture.GetDbContext();
        db.Users.Add(new User
        {
            Id = userId,
            Email = $"{prefix}-{userId:N}@test.com",
            FirebaseUid = firebaseUid,
        });
        await db.SaveChangesAsync();
        return (userId, firebaseUid);
    }
}
