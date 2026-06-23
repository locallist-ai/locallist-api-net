using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using LocalList.API.NET.Features.Chat;
using LocalList.API.NET.Features.Cities;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.Tests.Features;

/// <summary>
/// Gate de cobertura: solo las ciudades de la allowlist <c>Coverage:LiveCities</c>
/// (default <c>["Miami"]</c> en tests) se exponen y se planifican. El caveat clave:
/// una ciudad de TEST puede tener places en la admin y aun así NO estar cubierta —
/// la allowlist manda sobre "tiene places".
/// </summary>
public class CoverageGateTests(ApiFixture fixture) : IClassFixture<ApiFixture>, IDisposable
{
    private const string Miami = "Miami";

    public void Dispose()
    {
        fixture.FakeGemini.Responder = null;
        fixture.FakeGemini.Calls.Clear();
    }

    // ── GET /cities/live ──────────────────────────────────────────────────────

    [Fact]
    public async Task Live_ReturnsMiami_AndExcludesTestCityWithPlaces()
    {
        // Ciudad de TEST: existe como seed con places, pero NO está en la allowlist.
        var testCity = $"Testville_{Guid.NewGuid():N}";
        await SeedCityWithPlaces(testCity, 3);

        var client = fixture.CreateClient();
        var response = await client.GetAsync("/cities/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<LiveResponse>();
        Assert.NotNull(body);
        Assert.Contains(body!.Cities, c => c.Name == Miami);
        // La ciudad de test NO debe aparecer aunque tenga places.
        Assert.DoesNotContain(body.Cities, c => c.Name == testCity);
    }

    [Fact]
    public async Task Live_EnrichesWithSeedRow_WhenPresent()
    {
        // Si Miami existe como seed con country, la respuesta lo trae. Find-or-create
        // + set country (otros tests pueden haber sembrado Miami sin country).
        var db = fixture.GetDbContext();
        var miamiRow = await db.Cities.FirstOrDefaultAsync(c => c.NormalizedName == "miami" && c.Source == "seed");
        if (miamiRow == null)
        {
            miamiRow = new City
            {
                Id = Guid.NewGuid(),
                Name = Miami,
                NormalizedName = "miami",
                Source = "seed",
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Cities.Add(miamiRow);
        }
        miamiRow.Country = "US";
        await db.SaveChangesAsync();

        var client = fixture.CreateClient();
        var body = await (await client.GetAsync("/cities/live")).Content.ReadFromJsonAsync<LiveResponse>();
        var miami = body!.Cities.Single(c => c.Name == Miami);
        Assert.NotNull(miami.Id);
        Assert.Equal("US", miami.Country);
    }

    // ── POST /chat/turn ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Turn_PreSeededTestCity_WithPlaces_BlockedAndDoesNotSetCity()
    {
        // CAVEAT: la ciudad de test tiene places pero no es LIVE → aviso, sin ciudad.
        var testCity = $"Faketown_{Guid.NewGuid():N}";
        await SeedCityWithPlaces(testCity, 3);

        var client = fixture.CreateClient();
        var res = await client.PostAsJsonAsync("/chat/turn", new
        {
            preSeededSlots = new { city = testCity }
        });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("cityUnsupported").GetBoolean());

        // City slot debe quedar vacío (no se avanza el slot-filling).
        var slots = body.GetProperty("slots");
        var hasCity = slots.TryGetProperty("city", out var cityEl);
        Assert.True(!hasCity || string.IsNullOrEmpty(cityEl.GetString()));

        // El aviso ofrece Miami.
        var aiMsg = body.GetProperty("aiMessage").GetString();
        Assert.Contains(Miami, aiMsg!, StringComparison.OrdinalIgnoreCase);
        Assert.False(body.GetProperty("ready").GetBoolean());
    }

    [Fact]
    public async Task Turn_PreSeededMiami_ProceedsNormally()
    {
        var db = fixture.GetDbContext();
        if (!await db.Cities.AnyAsync(c => c.NormalizedName == "miami"))
        {
            db.Cities.Add(new City { Name = Miami, NormalizedName = "miami", Source = "seed" });
            await db.SaveChangesAsync();
        }

        var client = fixture.CreateClient();
        var res = await client.PostAsJsonAsync("/chat/turn", new { preSeededSlots = new { city = Miami } });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        // No bloqueado, ciudad asignada.
        var hasUnsupported = body.TryGetProperty("cityUnsupported", out var unsup) && unsup.GetBoolean();
        Assert.False(hasUnsupported);
        Assert.Equal(Miami, body.GetProperty("slots").GetProperty("city").GetString());
    }

    [Fact]
    public async Task Turn_FreeText_NonLiveCity_Blocked()
    {
        // Gemini extrae una ciudad no cubierta → bloqueo, sin avanzar.
        fixture.FakeGemini.Responder = _ => SlotExtraction(new
        {
            extracted = new { city = "Paris" },
            aiMessage = "Great, Paris!",
            nextQuestion = "days",
            quickReplies = Array.Empty<object>()
        });

        var client = fixture.CreateClient();
        var res = await client.PostAsJsonAsync("/chat/turn", new { message = "I want to visit Paris" });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("cityUnsupported").GetBoolean());

        var slots = body.GetProperty("slots");
        var hasCity = slots.TryGetProperty("city", out var cityEl);
        Assert.True(!hasCity || string.IsNullOrEmpty(cityEl.GetString()));
    }

    // ── POST /chat/generate ───────────────────────────────────────────────────

    [Fact]
    public async Task Generate_TestCitySession_WithPlaces_Returns400CityUnsupported_NoGeneration()
    {
        // CAVEAT: ciudad de test con places en una sesión ready. NO debe generar
        // plan; debe responder estructurado (no 404 seco, no LLM call).
        var testCity = $"Mocktown_{Guid.NewGuid():N}";
        await SeedCityWithPlaces(testCity, 4);

        var db = fixture.GetDbContext();
        var session = new ChatSession
        {
            Status = "ready",
            TurnCount = 3,
            // Serializa ChatSlots real (PascalCase) para que GetSlots lo lea — el
            // gate de cobertura inspecciona slots.City directamente.
            SlotsJson = JsonSerializer.Serialize(new ChatSlots
            {
                City = testCity, Days = 2, GroupType = "couple",
                Categories = new() { "food" }, Budget = "moderate"
            })
        };
        db.ChatSessions.Add(session);
        await db.SaveChangesAsync();

        fixture.FakeGemini.Calls.Clear();

        var client = fixture.CreateClient();
        var response = await client.PostAsJsonAsync("/chat/generate", new { sessionId = session.Id });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("city_unsupported", body.GetProperty("error").GetString());
        Assert.Contains(Miami, body.GetProperty("message").GetString()!, StringComparison.OrdinalIgnoreCase);

        // No se llamó al LLM (no se intentó generar) y no se persistió ningún plan.
        Assert.Empty(fixture.FakeGemini.Calls);
        var after = fixture.GetDbContext();
        Assert.False(await after.Plans.AnyAsync(p => p.City == testCity));
    }

    [Fact]
    public async Task Generate_MiamiSession_FullFlowOk()
    {
        await SeedCityWithPlaces(Miami, 3);

        var db = fixture.GetDbContext();
        var session = new ChatSession
        {
            Status = "ready",
            TurnCount = 3,
            SlotsJson = JsonSerializer.Serialize(new ChatSlots
            {
                City = Miami, Days = 1, GroupType = "couple",
                Categories = new() { "food" }, Budget = "moderate"
            })
        };
        db.ChatSessions.Add(session);
        await db.SaveChangesAsync();

        fixture.FakeGemini.Responder = _ => SlotExtraction(new
        {
            days = 1,
            categories = new[] { "food" },
            vibes = Array.Empty<string>(),
            groupType = "couple",
            planName = "Miami Day",
            maxStopsPerDay = 3
        });

        var client = fixture.CreateClient();
        var response = await client.PostAsJsonAsync("/chat/generate", new { sessionId = session.Id });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(Miami, body.GetProperty("plan").GetProperty("city").GetString());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task SeedCityWithPlaces(string cityName, int placeCount)
    {
        var db = fixture.GetDbContext();
        var normalized = CityNameNormalizer.Normalize(cityName);
        if (!await db.Cities.AnyAsync(c => c.NormalizedName == normalized))
        {
            db.Cities.Add(new City
            {
                Id = Guid.NewGuid(),
                Name = cityName,
                NormalizedName = normalized,
                Source = "seed",
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        var tag = Guid.NewGuid().ToString("N")[..8];
        for (int i = 0; i < placeCount; i++)
        {
            db.Places.Add(new Place
            {
                Id = Guid.NewGuid(),
                Name = $"Coverage Place {tag}-{i}",
                Category = "food",
                City = cityName,
                WhyThisPlace = "Seeded for coverage tests",
                Status = "published",
                BestTimes = new List<string> { "any" },
                Latitude = 25.77m + (decimal)(i * 0.01),
                Longitude = -80.19m + (decimal)(i * 0.01),
                GooglePlaceId = $"gpid-cov-{tag}-{i}",
            });
        }
        await db.SaveChangesAsync();
    }

    /// <summary>Envuelve un objeto como respuesta Gemini (texto del primer candidate).</summary>
    private static HttpResponseMessage SlotExtraction(object payload)
    {
        var envelope = new
        {
            candidates = new[]
            {
                new { content = new { parts = new[] { new { text = JsonSerializer.Serialize(payload) } } } }
            }
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(envelope), Encoding.UTF8, "application/json")
        };
    }

    private class LiveResponse
    {
        public List<LiveCityDto> Cities { get; set; } = new();
    }
}
