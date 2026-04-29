using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using LocalList.API.NET.Features.Cities;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.Tests.Features;

/// <summary>
/// Tests del registry de ciudades (Pablo 2026-04-27 — custom builder permite
/// añadir ciudades nuevas).
/// </summary>
public class CitiesTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    // ── CityNameValidator ─────────────────────────────────────────────────

    [Fact]
    public void IsLikelyRealCity_Mal_ReturnsFalse()
    {
        Assert.False(CityNameValidator.IsLikelyRealCity("mal", out var reason));
        Assert.NotNull(reason);
    }

    [Fact]
    public void IsLikelyRealCity_Lima_ReturnsTrue()
    {
        Assert.True(CityNameValidator.IsLikelyRealCity("lima", out var reason));
        Assert.Null(reason);
    }

    [Fact]
    public void IsLikelyRealCity_TwoChars_ReturnsFalse()
    {
        Assert.False(CityNameValidator.IsLikelyRealCity("mi", out _));
    }

    [Fact]
    public void IsLikelyRealCity_NoVowel_ReturnsFalse()
    {
        Assert.False(CityNameValidator.IsLikelyRealCity("xxx", out _));
    }

    // ── NormalizeName ──────────────────────────────────────────────────────

    [Fact]
    public void NormalizeName_StripsAccentsAndLowercases()
    {
        Assert.Equal("malaga", CityNameNormalizer.Normalize("Málaga"));
        Assert.Equal("sao paulo", CityNameNormalizer.Normalize("São Paulo"));
        Assert.Equal("miami", CityNameNormalizer.Normalize("MIAMI"));
        Assert.Equal("miami", CityNameNormalizer.Normalize("  Miami  "));
    }

    [Fact]
    public void NormalizeName_StripsControlAndFormatChars()
    {
        // Zero-width joiner (U+200D, Cf) entre caracteres no debe sobrevivir.
        Assert.Equal("miami", CityNameNormalizer.Normalize("Mi\u200Dami"));
        // Bell control char (U+0007, Cc).
        Assert.Equal("miami", CityNameNormalizer.Normalize("Mi\u0007ami"));
    }

    [Fact]
    public void CitiesController_NormalizeName_DelegatesToHelper()
    {
        // Compatibility wrapper sigue funcionando para callers existentes.
        Assert.Equal("malaga", CitiesController.NormalizeName("Málaga"));
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
    public async Task PostCity_OneCharName_Returns400()
    {
        var (userId, fbUid) = await CreateUser("city-tooshort");
        var client = fixture.CreateAuthenticatedClient(userId, fbUid);

        var response = await client.PostAsJsonAsync("/cities", new { name = "M" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostCity_TwoCharName_Returns400()
    {
        var (userId, fbUid) = await CreateUser("city-twochars");
        var client = fixture.CreateAuthenticatedClient(userId, fbUid);

        var response = await client.PostAsJsonAsync("/cities", new { name = "Mi" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostCity_NoVowel_Returns400()
    {
        var (userId, fbUid) = await CreateUser("city-novowel");
        var client = fixture.CreateAuthenticatedClient(userId, fbUid);

        var response = await client.PostAsJsonAsync("/cities", new { name = "xxx" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostCity_BlocklistedName_Returns400()
    {
        var (userId, fbUid) = await CreateUser("city-blocklist");
        var client = fixture.CreateAuthenticatedClient(userId, fbUid);

        var r1 = await client.PostAsJsonAsync("/cities", new { name = "Mal" });
        Assert.Equal(HttpStatusCode.BadRequest, r1.StatusCode);

        var r2 = await client.PostAsJsonAsync("/cities", new { name = "asdf" });
        Assert.Equal(HttpStatusCode.BadRequest, r2.StatusCode);
    }

    [Fact]
    public async Task PostCity_LegitimateShortNames_Return201()
    {
        var (userId, fbUid) = await CreateUser("city-legit");
        var client = fixture.CreateAuthenticatedClient(userId, fbUid);

        foreach (var city in new[] { "Lima", "Roma", "Lyon", "Bern" })
        {
            var name = $"{city}_{Guid.NewGuid():N}";
            var response = await client.PostAsJsonAsync("/cities", new { name });
            Assert.True(response.IsSuccessStatusCode, $"{city} should be accepted, got {response.StatusCode}");
        }
    }

    [Fact]
    public async Task PostCity_WhitespaceOnly_Returns400()
    {
        var (userId, fbUid) = await CreateUser("city-whitespace");
        var client = fixture.CreateAuthenticatedClient(userId, fbUid);

        var response = await client.PostAsJsonAsync("/cities", new { name = "  " });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostCity_DuplicateName_DoesNotCreateExtraRow()
    {
        // B2: idempotency must prove DB count == 1 (the existing test only
        // checks same Id is returned — could mask a duplicate that's deduped
        // on read).
        var (userId, fbUid) = await CreateUser("city-dup-count");
        var client = fixture.CreateAuthenticatedClient(userId, fbUid);
        var name = $"DupCountCity_{Guid.NewGuid():N}";

        await client.PostAsJsonAsync("/cities", new { name });
        await client.PostAsJsonAsync("/cities", new { name = name.ToUpperInvariant() });

        var db = fixture.GetDbContext();
        var normalized = CityNameNormalizer.Normalize(name);
        var count = await db.Cities.CountAsync(c => c.NormalizedName == normalized);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task PostCity_ConcurrentRequests_RemainsIdempotent()
    {
        // A3: race regression test. Two concurrent POSTs with the same
        // normalized name must both return 2xx with same Id, NOT 500.
        // Without the DbUpdateException handler this fires the unique
        // violation and surfaces InternalServerError.
        var (userId, fbUid) = await CreateUser("city-race");
        var client = fixture.CreateAuthenticatedClient(userId, fbUid);
        var name = $"RaceCity_{Guid.NewGuid():N}";
        var payload = new { name };

        var tasks = Enumerable.Range(0, 2)
            .Select(_ => client.PostAsJsonAsync("/cities", payload))
            .ToArray();
        var responses = await Task.WhenAll(tasks);

        foreach (var r in responses)
            Assert.True(r.IsSuccessStatusCode, $"Expected 2xx, got {r.StatusCode}");

        var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadFromJsonAsync<CityDto>()));
        Assert.NotNull(bodies[0]);
        Assert.NotNull(bodies[1]);
        Assert.Equal(bodies[0]!.Id, bodies[1]!.Id);

        // DB must have exactly one row.
        var db = fixture.GetDbContext();
        var normalized = CityNameNormalizer.Normalize(name);
        var count = await db.Cities.CountAsync(c => c.NormalizedName == normalized);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task PostCity_TokenWithoutDbUser_Returns401()
    {
        // C5: válid JWT (correctamente firmado) pero el userId no corresponde
        // a ningún User en DB. El controller debe rechazar antes de persistir
        // una fila huérfana con CreatedById=null.
        var phantomUserId = Guid.NewGuid();
        var phantomFbUid = $"fb-phantom-{phantomUserId:N}";
        var client = fixture.CreateAuthenticatedClient(phantomUserId, phantomFbUid);

        var response = await client.PostAsJsonAsync("/cities", new { name = "PhantomCity" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Search_AccentInsensitivePrefix_ReturnsCity()
    {
        // B2: end-to-end prueba que NormalizeName está realmente aplicado en
        // la query, no sólo en el helper estático.
        var name = $"México_{Guid.NewGuid():N}";
        var db = fixture.GetDbContext();
        db.Cities.Add(new City
        {
            Id = Guid.NewGuid(),
            Name = name,
            NormalizedName = CityNameNormalizer.Normalize(name),
            Source = "seed",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var client = fixture.CreateClient();
        // Query "MEX" (uppercase, no accent) debe encontrar "México".
        var response = await client.GetAsync("/cities/search?q=MEX");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<SearchResponse>();
        Assert.NotNull(body);
        Assert.Contains(body!.Cities, c => c.Name == name);
    }

    [Fact]
    public async Task Search_EmptyQuery_Returns400()
    {
        var client = fixture.CreateClient();
        var response = await client.GetAsync("/cities/search");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Search_TooShortQuery_Returns400()
    {
        // Hardening: empty + 1-char queries hacen sort over full table sin
        // índice. Reject < MinSearchLength (2).
        var client = fixture.CreateClient();
        var response = await client.GetAsync("/cities/search?q=a");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Search_TooLongQuery_Returns400()
    {
        // DoS guard: cap input at MaxRawLength (64).
        var longQ = new string('a', 65);
        var client = fixture.CreateClient();
        var response = await client.GetAsync($"/cities/search?q={longQ}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Visibilidad seed vs usuario ────────────────────────────────────────

    [Fact]
    public async Task Search_SeedCity_VisibleToAnonymous()
    {
        var name = $"SeedVis_{Guid.NewGuid():N}";
        var db = fixture.GetDbContext();
        db.Cities.Add(new City
        {
            Id = Guid.NewGuid(),
            Name = name,
            NormalizedName = CityNameNormalizer.Normalize(name),
            Source = "seed",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var client = fixture.CreateClient();
        var response = await client.GetAsync($"/cities/search?q={name[..6]}");
        var body = await response.Content.ReadFromJsonAsync<SearchResponse>();
        Assert.Contains(body!.Cities, c => c.Name == name);
    }

    [Fact]
    public async Task Search_UserCity_VisibleToCreator()
    {
        var (userId, fbUid) = await CreateUser("vis-creator");
        var client = fixture.CreateAuthenticatedClient(userId, fbUid);

        var name = $"UserCity_{Guid.NewGuid():N}";
        await client.PostAsJsonAsync("/cities", new { name });

        var response = await client.GetAsync($"/cities/search?q={name[..8]}");
        var body = await response.Content.ReadFromJsonAsync<SearchResponse>();
        Assert.Contains(body!.Cities, c => c.Name == name);
    }

    [Fact]
    public async Task Search_UserCity_NotVisibleToOtherUser()
    {
        var (ownerUserId, ownerFbUid) = await CreateUser("vis-owner");
        var ownerClient = fixture.CreateAuthenticatedClient(ownerUserId, ownerFbUid);

        var name = $"PrivCity_{Guid.NewGuid():N}";
        await ownerClient.PostAsJsonAsync("/cities", new { name });

        var (otherUserId, otherFbUid) = await CreateUser("vis-other");
        var otherClient = fixture.CreateAuthenticatedClient(otherUserId, otherFbUid);

        var response = await otherClient.GetAsync($"/cities/search?q={name[..8]}");
        var body = await response.Content.ReadFromJsonAsync<SearchResponse>();
        Assert.DoesNotContain(body!.Cities, c => c.Name == name);
    }

    [Fact]
    public async Task Search_UserCity_NotVisibleToAnonymous()
    {
        var (userId, fbUid) = await CreateUser("vis-anon");
        var userClient = fixture.CreateAuthenticatedClient(userId, fbUid);

        var name = $"AnonCity_{Guid.NewGuid():N}";
        await userClient.PostAsJsonAsync("/cities", new { name });

        var anonClient = fixture.CreateClient();
        var response = await anonClient.GetAsync($"/cities/search?q={name[..8]}");
        var body = await response.Content.ReadFromJsonAsync<SearchResponse>();
        Assert.DoesNotContain(body!.Cities, c => c.Name == name);
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
