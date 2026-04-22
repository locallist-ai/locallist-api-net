using System.Net;
using System.Net.Http.Headers;
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
        fixture.FakeEmbeddings.Responder = null;
        fixture.FakeEmbeddings.Calls.Clear();
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

    [Fact]
    public async Task Chat_WithEmbeddedPlaces_ActivatesRagPath()
    {
        // Seedeamos 4 places y los reindexamos via /admin/places/reindex-embeddings.
        // Tras el reindex, el BuilderController detectará embeddedCount>=3 y activará
        // el path RAG (embed query + cosine distance + rerank).
        var seededIds = await SeedPublishedMiamiPlaces(4);
        var adminClient = CreateAdminClient();
        var reindex = await adminClient.PostAsync("/admin/places/reindex-embeddings", content: null);
        Assert.Equal(HttpStatusCode.OK, reindex.StatusCode);

        var extracted = new
        {
            days = 1,
            categories = new[] { "food" },
            vibes = new[] { "romantic" },
            groupType = "couple",
            planName = "RAG Test",
            maxStopsPerDay = 3,
        };
        fixture.FakeGemini.Responder = _ => GeminiOk(JsonSerializer.Serialize(extracted));

        var client = fixture.CreateClient();
        var response = await client.PostAsJsonAsync("/builder/chat", new
        {
            message = "romantic dinner in Wynwood",
            tripContext = new { city = Miami },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var stops = body.GetProperty("stops");
        Assert.True(stops.GetArrayLength() >= 1, "RAG path debería devolver al menos una stop");

        // Validar que EmbeddingService hizo al menos una llamada extra al fake
        // (la del reindex + la del query del chat). El reindex hace 1 call batch.
        // Sin esto no podemos distinguir RAG activo de fallback activo.
        Assert.True(fixture.FakeEmbeddings.Calls.Count >= 2,
            $"Se esperaban >=2 llamadas al embedding fake (reindex+chat), hubo {fixture.FakeEmbeddings.Calls.Count}");
    }

    [Fact]
    public async Task Chat_EmbeddingProviderFailsOnQuery_FallsBackToKeywords()
    {
        // Catálogo ya indexado (>=3 places con embedding).
        await SeedPublishedMiamiPlaces(4);
        var adminClient = CreateAdminClient();
        var reindex = await adminClient.PostAsync("/admin/places/reindex-embeddings", content: null);
        Assert.Equal(HttpStatusCode.OK, reindex.StatusCode);

        // Ahora rompemos el embedding para la query del chat (sólo esta llamada).
        // Nota: el reindex ya ocurrió con responder=null → handler default OK.
        fixture.FakeEmbeddings.Responder = _ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("Bad Gateway", Encoding.UTF8, "text/plain"),
        };

        var extracted = new
        {
            days = 1,
            categories = new[] { "food" },
            vibes = new string[] { },
            groupType = "couple",
            planName = "Fallback Test",
            maxStopsPerDay = 3,
        };
        fixture.FakeGemini.Responder = _ => GeminiOk(JsonSerializer.Serialize(extracted));

        var client = fixture.CreateClient();
        var response = await client.PostAsJsonAsync("/builder/chat", new
        {
            message = "some food in Miami",
            tripContext = new { city = Miami },
        });

        // Fallback keyword activo → 200 con plan válido, no 500.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("plan", out _), "Fallback debería devolver un plan");
    }

    [Fact]
    public async Task Chat_GreetingMessage_KeywordFallback_GeneratesDescriptivePlanName()
    {
        // Regresión PR #B: un mensaje trivial ("Hola") ya no debe aparecer como plan.Name
        // cuando el fallback keyword se activa. BuildPlanName sintetiza a partir de vibes/city.
        await SeedPublishedMiamiPlaces(3);

        // Gemini devuelve 502 → ExtractWithKeywords toma el control. Le pasamos
        // tripContext.preferences=["adventure"] para que el mapa de seed añada
        // categories=outdoors,culture y vibes=adventure.
        fixture.FakeGemini.Responder = _ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("Bad Gateway", Encoding.UTF8, "text/plain")
        };

        var client = fixture.CreateClient();
        var response = await client.PostAsJsonAsync("/builder/chat", new
        {
            message = "Hola",
            tripContext = new
            {
                city = Miami,
                days = 2,
                groupType = "family-kids",
                preferences = new[] { "adventure" }
            }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var planName = body.GetProperty("plan").GetProperty("name").GetString() ?? "";

        Assert.DoesNotContain("Hola", planName, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Miami", planName);
        // Debe venir de vibes (adventure) o duration (2-day).
        Assert.Contains("2-day", planName);
    }

    [Fact]
    public async Task Chat_KeywordFallback_SeedsCategoriesFromPreferences()
    {
        // Regresión PR #B: preferences=["adventure"] debe propagarse a categories=outdoors/culture
        // en el fallback keyword — sin esto los planes family-adventure salían con wellness/food genérico.
        await SeedPublishedMiamiPlaces(3);

        fixture.FakeGemini.Responder = _ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("", Encoding.UTF8, "text/plain")
        };

        var client = fixture.CreateClient();
        var response = await client.PostAsJsonAsync("/builder/chat", new
        {
            // Mensaje SIN keywords de categories — todo lo que contenga categories tiene que venir de preferences.
            message = "plan",
            tripContext = new
            {
                city = Miami,
                days = 1,
                groupType = "couple",
                preferences = new[] { "relax" }
            }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        // La description incluye top categorías: si seed funcionó, contiene "wellness" o "coffee".
        var description = body.GetProperty("plan").GetProperty("description").GetString() ?? "";
        var hasRelaxCategory = description.Contains("wellness", StringComparison.OrdinalIgnoreCase)
                            || description.Contains("coffee", StringComparison.OrdinalIgnoreCase);
        Assert.True(hasRelaxCategory,
            $"Se esperaba 'wellness' o 'coffee' en description, llegó: '{description}'");
    }

    // ── Parte C: family hard filters + matrix timeBlock ─────────────────────

    [Fact]
    public async Task Chat_GroupTypeFamily_PlanHasZeroNightlife()
    {
        // Mezcla: 3 places family-suitable en categorías OK para family + 2 nightlife adults-only.
        // Con groupType=family-kids, el scheduler debe excluir los nightlife por matrix
        // categoría×timeBlock + el rerank los pone al fondo por ScoreSuitableFor=0.
        await SeedPlace("FamilyCoffee", "coffee", "morning", new List<string> { "family", "kids" });
        await SeedPlace("FamilyCulture", "culture", "afternoon", new List<string> { "family" });
        await SeedPlace("FamilyFood", "food", "lunch", new List<string> { "family", "all-ages" });
        await SeedPlace("Bar1", "nightlife", "evening", new List<string> { "adults-only" });
        await SeedPlace("Bar2", "nightlife", "evening", new List<string> { "21+" });

        var extracted = new
        {
            days = 1,
            categories = new[] { "food", "coffee", "culture" },
            vibes = new string[] { },
            groupType = "family-kids",
            planName = "Family Miami Day",
            maxStopsPerDay = 4,
        };
        fixture.FakeGemini.Responder = _ => GeminiOk(JsonSerializer.Serialize(extracted));

        var client = fixture.CreateClient();
        var response = await client.PostAsJsonAsync("/builder/chat", new
        {
            message = "family trip with kids",
            tripContext = new { city = Miami, groupType = "family-kids", days = 1 },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var stops = body.GetProperty("stops").EnumerateArray().ToList();
        Assert.NotEmpty(stops);
        foreach (var s in stops)
        {
            var category = s.GetProperty("place").GetProperty("category").GetString() ?? "";
            Assert.False(
                string.Equals(category, "nightlife", StringComparison.OrdinalIgnoreCase),
                $"Se esperaba 0 stops de nightlife para family-kids, apareció {category}");
        }
    }

    [Fact]
    public async Task Chat_GroupTypeFamily_TooFewCandidates_SoftFallback_Returns200()
    {
        // Catálogo SOLO con adults-only nightlife. Family request no tiene opciones estrictas,
        // soft fallback debería al menos devolver 200 con los stops disponibles (o vacío) sin crash.
        await SeedPlace("Bar1", "nightlife", "evening", new List<string> { "adults-only" });
        await SeedPlace("Bar2", "nightlife", "evening", new List<string> { "21+" });
        await SeedPlace("Bar3", "nightlife", "evening", new List<string> { "adults-only" });

        var extracted = new
        {
            days = 1,
            categories = new[] { "food", "coffee" },
            vibes = new string[] { },
            groupType = "family-kids",
            planName = "Family Impossible",
            maxStopsPerDay = 3,
        };
        fixture.FakeGemini.Responder = _ => GeminiOk(JsonSerializer.Serialize(extracted));

        var client = fixture.CreateClient();
        var response = await client.PostAsJsonAsync("/builder/chat", new
        {
            message = "family",
            tripContext = new { city = Miami, groupType = "family-kids", days = 1 },
        });

        // El pipeline no debe crashear aunque no haya candidatos family. 200 OK con stops=[]
        // es aceptable, o stops con warning de soft fallback.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("plan", out _));
    }

    [Fact]
    public async Task Schedule_NightlifeWithAnyBestTime_DoesNotAppearInLunchSlot()
    {
        // nightlife con BestTime=any colaría en lunch por la regla BestTime legacy.
        // La matrix categoría×timeBlock filtra: lunch solo admite food/coffee.
        await SeedPlace("CoffeeA", "coffee", "any");
        await SeedPlace("FoodA", "food", "any");
        await SeedPlace("LatinBar", "nightlife", "any");

        var extracted = new
        {
            days = 1,
            categories = new[] { "food", "coffee", "nightlife" },
            vibes = new string[] { },
            groupType = "couple",
            planName = "Test",
            maxStopsPerDay = 5,
        };
        fixture.FakeGemini.Responder = _ => GeminiOk(JsonSerializer.Serialize(extracted));

        var client = fixture.CreateClient();
        var response = await client.PostAsJsonAsync("/builder/chat", new
        {
            message = "a bit of everything",
            tripContext = new { city = Miami, groupType = "couple", days = 1 },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var stops = body.GetProperty("stops").EnumerateArray().ToList();
        var lunchStops = stops.Where(s =>
            string.Equals(s.GetProperty("timeBlock").GetString(), "lunch", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Si hubo un lunch slot, nightlife NO debe aparecer allí.
        foreach (var ls in lunchStops)
        {
            var cat = ls.GetProperty("place").GetProperty("category").GetString() ?? "";
            Assert.False(
                string.Equals(cat, "nightlife", StringComparison.OrdinalIgnoreCase),
                $"nightlife no debería aparecer en lunch, llegó {cat}");
        }
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

    /// <summary>
    /// Inserta un Place con campos customizables — útil para probar Parte C del Builder
    /// (SuitableFor, Category, BestTime específicos por caso).
    /// </summary>
    private async Task<Guid> SeedPlace(
        string name,
        string category = "Food",
        string bestTime = "any",
        List<string>? suitableFor = null)
    {
        var db = fixture.GetDbContext();
        var tag = Guid.NewGuid().ToString("N")[..8];
        var id = Guid.NewGuid();
        db.Places.Add(new Place
        {
            Id = id,
            Name = $"{name}-{tag}",
            Category = category,
            City = Miami,
            WhyThisPlace = $"Seed for {name}",
            Status = "published",
            BestTime = bestTime,
            Latitude = 25.77m,
            Longitude = -80.19m,
            GooglePlaceId = $"gpid-{tag}",
            SuitableFor = suitableFor,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<List<Guid>> SeedPublishedMiamiPlaces(int count)
    {
        var db = fixture.GetDbContext();
        var tag = Guid.NewGuid().ToString("N")[..8];
        var ids = new List<Guid>();
        for (int i = 0; i < count; i++)
        {
            var id = Guid.NewGuid();
            ids.Add(id);
            db.Places.Add(new Place
            {
                Id = id,
                Name = $"Builder Seed {tag}-{i}",
                Category = "Food",
                City = Miami,
                WhyThisPlace = "Seeded para test de /builder/chat",
                Status = "published",
                BestTime = "any",
                Latitude = 25.77m + (decimal)(i * 0.01),
                Longitude = -80.19m + (decimal)(i * 0.01),
                GooglePlaceId = $"gpid-builder-{tag}-{i}",
            });
        }
        await db.SaveChangesAsync();
        return ids;
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
            Role = "admin",
        });
        db.SaveChanges();

        var client = fixture.CreateClient();
        var token = fixture.CreateToken(adminFbUid, adminEmail);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
