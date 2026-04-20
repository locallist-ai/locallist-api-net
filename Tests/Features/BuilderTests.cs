using System.Net;
using System.Text;
using System.Text.Json;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.Tests.Features;

/// <summary>
/// Tests de integración del endpoint <c>POST /builder/chat</c>.
///
/// Cubre tres flujos críticos:
///   1. Happy path: Gemini responde con JSON válido, se genera el plan.
///   2. Gemini falla con 502: el controller no revienta, cae a fallback por keywords.
///   3. Gemini devuelve texto no-JSON: también cae a keywords.
///
/// El HTTP saliente a Gemini se intercepta con <see cref="FakeGeminiHandler"/>
/// registrado en <see cref="ApiFixture"/>. Cada test fija <c>Responder</c> al
/// entrar y lo limpia al salir para no contaminar a otros tests.
/// </summary>
public class BuilderTests(ApiFixture fixture) : IClassFixture<ApiFixture>, IDisposable
{
    private const string Miami = "Miami";

    public void Dispose()
    {
        // Aseguramos no contaminar otros tests que dependan del handler por defecto.
        fixture.FakeGemini.Responder = null;
        fixture.FakeGemini.Calls.Clear();
    }

    [Fact]
    public async Task Chat_HappyPath_ReturnsPlanWithStops()
    {
        await SeedPublishedMiamiPlaces(3);

        var extracted = new
        {
            days = 1,
            categories = new[] { "food" },
            vibes = new[] { "romantic" },
            groupType = "couple",
            planName = "Test",
            maxStopsPerDay = 4
        };
        fixture.FakeGemini.Responder = _ => GeminiOk(JsonSerializer.Serialize(extracted));

        var client = fixture.CreateClient();
        var response = await client.PostAsJsonAsync("/builder/chat", new
        {
            message = "Plan romántico de comida en Miami",
            tripContext = new { city = Miami }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("plan", out var plan), "Respuesta sin propiedad plan");
        Assert.Equal(Miami, plan.GetProperty("city").GetString());
        var stops = body.GetProperty("stops");
        Assert.True(stops.GetArrayLength() >= 1, "Se esperaba al menos una stop en el plan");
    }

    [Fact]
    public async Task Chat_Gemini502_FallsBackToKeywords()
    {
        await SeedPublishedMiamiPlaces(3);

        fixture.FakeGemini.Responder = _ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("Bad Gateway", Encoding.UTF8, "text/plain")
        };

        var client = fixture.CreateClient();
        // "restaurant" activa el keyword fallback con categoría food.
        var response = await client.PostAsJsonAsync("/builder/chat", new
        {
            message = "Buen restaurant en Miami",
            tripContext = new { city = Miami }
        });

        // Gemini peta → AiProviderService captura HttpRequestException y tira de keywords.
        // El controller devuelve 200 con plan generado.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("plan", out _), "Respuesta sin plan tras fallback keywords");
    }

    [Fact]
    public async Task Chat_GeminiReturnsMalformedJson_FallsBackToKeywords()
    {
        await SeedPublishedMiamiPlaces(3);

        // Simulamos que Gemini responde 200 pero el "text" no es JSON parseable.
        // El JsonDocument.Parse de la envolvente sí debe funcionar; lo que
        // falla es ParseAiResponse al hacer Deserialize<ExtractedPreferences>.
        var geminiEnvelope = new
        {
            candidates = new[]
            {
                new
                {
                    content = new
                    {
                        parts = new[] { new { text = "esto no es un JSON válido {{{" } }
                    }
                }
            }
        };
        fixture.FakeGemini.Responder = _ => GeminiOk(JsonSerializer.Serialize(geminiEnvelope));

        var client = fixture.CreateClient();
        var response = await client.PostAsJsonAsync("/builder/chat", new
        {
            message = "cafe por la mañana en Miami",
            tripContext = new { city = Miami }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("plan", out _), "Respuesta sin plan tras malformed JSON");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static HttpResponseMessage GeminiOk(string embeddedText)
    {
        // Envolvemos el JSON esperado dentro del shape real de Gemini:
        //   { candidates:[{ content:{ parts:[{ text:"{...}" }]}}]}
        var envelope = new
        {
            candidates = new[]
            {
                new
                {
                    content = new
                    {
                        parts = new[] { new { text = embeddedText } }
                    }
                }
            }
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(envelope), Encoding.UTF8, "application/json")
        };
    }

    private async Task SeedPublishedMiamiPlaces(int count)
    {
        var db = fixture.GetDbContext();
        var tag = Guid.NewGuid().ToString("N")[..8];
        for (int i = 0; i < count; i++)
        {
            db.Places.Add(new Place
            {
                Id = Guid.NewGuid(),
                Name = $"Builder Seed {tag}-{i}",
                Category = "Food",
                City = Miami,
                WhyThisPlace = "Seeded para test de /builder/chat",
                Status = "published",
                BestTime = "any",
                Latitude = 25.77m + (decimal)(i * 0.01),
                Longitude = -80.19m + (decimal)(i * 0.01),
            });
        }
        await db.SaveChangesAsync();
    }
}
