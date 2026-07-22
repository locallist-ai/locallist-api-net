using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LocalList.API.NET.Shared.AI.Services;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.Tests.Features;

/// <summary>
/// Tests end-to-end de <c>POST /builder/chat</c> para el fix de calidad de planes
/// (fix/plan-quality-params): los parámetros que el usuario elige en el wizard
/// (categorías, días, budget) tienen que verse reflejados en el plan generado,
/// y la misma petición tiene que producir el mismo plan (semilla determinista).
///
/// DB real (ApiFixture = Testcontainers PostgreSQL); Gemini/embeddings/Mapbox
/// interceptados con los fakes del fixture. Cada test usa una ciudad aislada
/// para no contaminarse con el catálogo Miami compartido de otros tests.
/// </summary>
public class PlanQualityTests(ApiFixture fixture) : IClassFixture<ApiFixture>, IDisposable
{
    public void Dispose()
    {
        fixture.FakeGemini.Responder = null;
        fixture.FakeGemini.Calls.Clear();
        fixture.FakeEmbeddings.Responder = null;
        fixture.FakeEmbeddings.Calls.Clear();
        fixture.FakeMapbox.AsyncResponder = null;
        fixture.FakeMapbox.Responder = null;
        lock (fixture.FakeMapbox.Calls) { fixture.FakeMapbox.Calls.Clear(); }
    }

    // ── 1. Categorías explícitas = filtro (reemplazan alucinaciones del LLM) ──

    [Fact]
    public async Task Builder_ExplicitFoodCategory_LlmHallucinatesNightlife_AllStopsAreFood()
    {
        // El usuario eligió SOLO "food" en el wizard; Gemini alucina "nightlife".
        // Antes: union → nightlife entraba al pool y podía aparecer en el plan.
        // Ahora: replace + gate → todos los stops son food.
        var city = IsolatedCity("gate");
        await SeedPlaces(city, "food", 4);
        await SeedPlaces(city, "nightlife", 4);

        fixture.FakeGemini.Responder = _ => GeminiOk(JsonSerializer.Serialize(new
        {
            days = 1,
            categories = new[] { "nightlife" }, // alucinación del LLM
            vibes = new string[] { },
            groupType = "couple",
            planName = "Test",
            maxStopsPerDay = 4,
        }));

        var client = await fixture.CreateGenerationClientAsync();
        var res = await client.PostAsJsonAsync("/builder/chat", new
        {
            message = "un buen día por la ciudad",
            tripContext = new { city, days = 1, groupType = "couple", categories = new[] { "food" } },
        });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var stops = body.GetProperty("stops").EnumerateArray().ToList();
        Assert.NotEmpty(stops);
        foreach (var s in stops)
        {
            var cat = s.GetProperty("place").GetProperty("category").GetString() ?? "";
            Assert.True(string.Equals(cat, "food", StringComparison.OrdinalIgnoreCase),
                $"El usuario pidió solo 'food' pero el plan trae '{cat}'");
        }
    }

    // ── 2. Gate de categoría también en la ruta RAG ────────────────────────────

    [Fact]
    public async Task Builder_RagPath_ExplicitCategory_ExcludesOtherCategories()
    {
        // Con catálogo embebido (RAG activo), la categoría explícita del wizard
        // gatea el retrieval — antes solo era señal blanda (peso 0.12 vs cosine 0.34)
        // y el top-ranked de otra categoría colaba en el plan.
        var city = IsolatedCity("rag");
        await SeedPlaces(city, "food", 4);
        await SeedPlaces(city, "culture", 4);
        await ReindexEmbeddings();

        fixture.FakeGemini.Responder = _ => GeminiOk(JsonSerializer.Serialize(new
        {
            days = 1,
            categories = new[] { "food", "culture" },
            vibes = new string[] { },
            groupType = "couple",
            planName = "RAG Gate Test",
            maxStopsPerDay = 3,
        }));

        var client = await fixture.CreateGenerationClientAsync();
        var res = await client.PostAsJsonAsync("/builder/chat", new
        {
            message = "food tour",
            tripContext = new { city, days = 1, groupType = "couple", categories = new[] { "food" } },
        });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        // RAG activo de verdad: reindex (1 call batch) + query embed (≥1 más).
        Assert.True(fixture.FakeEmbeddings.Calls.Count >= 2,
            $"Se esperaba path RAG activo (≥2 llamadas embedding), hubo {fixture.FakeEmbeddings.Calls.Count}");

        var stops = body.GetProperty("stops").EnumerateArray().ToList();
        Assert.NotEmpty(stops);
        foreach (var s in stops)
        {
            var cat = s.GetProperty("place").GetProperty("category").GetString() ?? "";
            Assert.True(string.Equals(cat, "food", StringComparison.OrdinalIgnoreCase),
                $"RAG path: el usuario pidió solo 'food' pero el plan trae '{cat}'");
        }
    }

    // ── 2b. Food exenta del gate: categoría explícita no-food sigue comiendo ──

    [Fact]
    public async Task Builder_RagPath_ExplicitCultureCategory_EveryDayHasFoodStop()
    {
        // Wizard con categories=["culture"] y catálogo con culture de sobra: el gate
        // duro no puede dejar el pool sin food, o EnsureFoodPerDay queda inoperante
        // y salen días enteros sin parada de comida (regresión vs main).
        var city = IsolatedCity("meals");
        await SeedPlaces(city, "culture", 6);
        await SeedPlaces(city, "food", 3);
        await SeedPlaces(city, "nightlife", 3);
        await ReindexEmbeddings();

        fixture.FakeGemini.Responder = _ => GeminiOk(JsonSerializer.Serialize(new
        {
            days = 2,
            categories = new[] { "culture" },
            vibes = new string[] { },
            groupType = "couple",
            planName = "Culture Meals Test",
            maxStopsPerDay = 3,
        }));

        var client = await fixture.CreateGenerationClientAsync();
        var res = await client.PostAsJsonAsync("/builder/chat", new
        {
            message = "dos días de museos",
            tripContext = new { city, days = 2, groupType = "couple", categories = new[] { "culture" } },
        });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var stops = body.GetProperty("stops").EnumerateArray().ToList();
        Assert.NotEmpty(stops);

        // El gate sigue aplicando a las no-food: solo culture o food, nada de nightlife.
        foreach (var s in stops)
        {
            var cat = s.GetProperty("place").GetProperty("category").GetString() ?? "";
            Assert.True(
                string.Equals(cat, "culture", StringComparison.OrdinalIgnoreCase)
                || string.Equals(cat, "food", StringComparison.OrdinalIgnoreCase),
                $"Con categories=['culture'] solo se esperaba culture+food, llegó '{cat}'");
        }

        // Invariante de EnsureFoodPerDay: cada día del plan tiene ≥1 parada food.
        var days = stops.GroupBy(s => s.GetProperty("dayNumber").GetInt32()).ToList();
        Assert.Equal(2, days.Count);
        foreach (var day in days)
        {
            Assert.True(
                day.Any(s => string.Equals(
                    s.GetProperty("place").GetProperty("category").GetString(), "food",
                    StringComparison.OrdinalIgnoreCase)),
                $"El día {day.Key} no tiene ninguna parada de comida");
        }
    }

    // ── 2c. Gate sobre el catálogo, no sobre el top-K por cosine ───────────────

    [Fact]
    public async Task Builder_RagPath_RequestedCategoryBeyondTopK_StillFillsPlan()
    {
        // Ciudad grande: 52 culture semánticamente pegados a la query saturan el
        // top-50 por cosine y los food quedan fuera, aunque el catálogo tiene
        // suficientes. La query de top-up por categoría explícita debe rescatarlos
        // — sin ella el gate cree que no hay food y mete el fallback mixto.
        var city = IsolatedCity("topk");
        await SeedPlaces(city, "culture", 52);
        await SeedPlaces(city, "food", 3);

        // Embeddings dirigidos: query y culture → e1 (distancia 0 entre sí);
        // food → e2 (distancia 1 de la query). Así el top-50 es 100% culture.
        fixture.FakeEmbeddings.Responder = req => DirectedEmbeddings(req);
        await ReindexEmbeddings();

        fixture.FakeGemini.Responder = _ => GeminiOk(JsonSerializer.Serialize(new
        {
            days = 1,
            categories = new[] { "food" },
            vibes = new string[] { },
            groupType = "couple",
            planName = "TopK Gate Test",
            maxStopsPerDay = 3,
        }));

        var client = await fixture.CreateGenerationClientAsync();
        var res = await client.PostAsJsonAsync("/builder/chat", new
        {
            message = "best eats in town",
            tripContext = new { city, days = 1, groupType = "couple", categories = new[] { "food" } },
        });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        // Path RAG activo de verdad: reindex + query embed.
        Assert.True(fixture.FakeEmbeddings.Calls.Count >= 2,
            $"Se esperaba path RAG activo (≥2 llamadas embedding), hubo {fixture.FakeEmbeddings.Calls.Count}");

        var stops = body.GetProperty("stops").EnumerateArray().ToList();
        Assert.NotEmpty(stops);
        foreach (var s in stops)
        {
            var cat = s.GetProperty("place").GetProperty("category").GetString() ?? "";
            Assert.True(string.Equals(cat, "food", StringComparison.OrdinalIgnoreCase),
                $"El catálogo tiene 3 food pero el plan trae '{cat}' — el gate miró solo el top-50");
        }
    }

    // ── 3. Budget tier del wizard influye en el plan ───────────────────────────

    [Fact]
    public async Task Builder_PremiumBudget_PrefersExpensivePlaces()
    {
        // Mismo catálogo food, mitad "$" mitad "$$$$". Con budget=premium los $$$$
        // deben dominar el plan (antes el tier se descartaba y el peso era 0.04).
        var city = IsolatedCity("budget");
        await SeedPlaces(city, "food", 4, priceRange: "$$$$", namePrefix: "Fancy");
        await SeedPlaces(city, "food", 4, priceRange: "$", namePrefix: "Cheap");
        await ReindexEmbeddings();

        fixture.FakeGemini.Responder = _ => GeminiOk(JsonSerializer.Serialize(new
        {
            days = 1,
            categories = new[] { "food" },
            vibes = new string[] { },
            groupType = "couple",
            planName = "Premium Test",
            maxStopsPerDay = 4,
        }));

        var client = await fixture.CreateGenerationClientAsync();
        var res = await client.PostAsJsonAsync("/builder/chat", new
        {
            message = "high end dining",
            tripContext = new
            {
                city, days = 1, groupType = "couple",
                categories = new[] { "food" }, budget = "premium",
            },
        });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var stops = body.GetProperty("stops").EnumerateArray().ToList();
        Assert.NotEmpty(stops);

        // needed=4 → los 2 primeros slots van garantizados al top del ranking, y con
        // budget premium el top son los $$$$ (delta 0.10 domina el ruido de cosine).
        var premiumStops = stops.Count(s =>
            (s.GetProperty("place").GetProperty("priceRange").GetString() ?? "") == "$$$$");
        Assert.True(premiumStops >= 2,
            $"Con budget=premium se esperaban ≥2 stops $$$$ de {stops.Count}, hubo {premiumStops}");
    }

    // ── 4. Determinismo end-to-end: misma petición → mismo plan ────────────────

    [Fact]
    public async Task Builder_SameRequest_TwiceProducesIdenticalStops()
    {
        var city = IsolatedCity("determ");
        await SeedPlaces(city, "food", 8);

        fixture.FakeGemini.Responder = _ => GeminiOk(JsonSerializer.Serialize(new
        {
            days = 2,
            categories = new[] { "food" },
            vibes = new string[] { },
            groupType = "couple",
            planName = "Determinism Test",
            maxStopsPerDay = 3,
        }));

        var request = new
        {
            message = "dos días de comida",
            tripContext = new { city, days = 2, groupType = "couple", categories = new[] { "food" } },
        };

        var client = await fixture.CreateGenerationClientAsync();
        var res1 = await client.PostAsJsonAsync("/builder/chat", request);
        var res2 = await client.PostAsJsonAsync("/builder/chat", request);
        Assert.Equal(HttpStatusCode.OK, res1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, res2.StatusCode);

        var stops1 = await ReadStops(res1);
        var stops2 = await ReadStops(res2);

        Assert.NotEmpty(stops1);
        Assert.Equal(stops1.Count, stops2.Count);
        for (int i = 0; i < stops1.Count; i++)
        {
            Assert.Equal(stops1[i].placeId,  stops2[i].placeId);
            Assert.Equal(stops1[i].day,      stops2[i].day);
            Assert.Equal(stops1[i].order,    stops2[i].order);
            Assert.Equal(stops1[i].arrival,  stops2[i].arrival);
        }
    }

    // ── 5. E2E combinado: días + categorías + budget respetados a la vez ──────

    [Fact]
    public async Task Builder_Wizard_EndToEnd_RespectsDaysCategoryAndBudget()
    {
        var city = IsolatedCity("e2e");
        await SeedPlaces(city, "food", 4, priceRange: "$$$$", namePrefix: "Fancy");
        await SeedPlaces(city, "food", 4, priceRange: "$", namePrefix: "Cheap");
        await SeedPlaces(city, "culture", 3, priceRange: "$$");
        await ReindexEmbeddings();

        // Gemini contradice al wizard en días y categorías — el contexto gana.
        fixture.FakeGemini.Responder = _ => GeminiOk(JsonSerializer.Serialize(new
        {
            days = 1,
            categories = new[] { "culture" },
            vibes = new string[] { },
            groupType = "couple",
            planName = "E2E Test",
            maxStopsPerDay = 3,
        }));

        var client = await fixture.CreateGenerationClientAsync();
        var res = await client.PostAsJsonAsync("/builder/chat", new
        {
            message = "escapada gastronómica",
            tripContext = new
            {
                city, days = 2, groupType = "couple",
                categories = new[] { "food" }, budget = "premium",
            },
        });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        // Días del wizard, no los de Gemini.
        Assert.Equal(2, body.GetProperty("plan").GetProperty("durationDays").GetInt32());

        var stops = body.GetProperty("stops").EnumerateArray().ToList();
        Assert.NotEmpty(stops);

        // Categorías: solo food (8 food ≥ 6 slots → gate duro, culture fuera).
        foreach (var s in stops)
        {
            var cat = s.GetProperty("place").GetProperty("category").GetString() ?? "";
            Assert.True(string.Equals(cat, "food", StringComparison.OrdinalIgnoreCase),
                $"E2E: se pidió 'food' y el plan trae '{cat}'");
        }

        // Budget: el top del ranking (slots garantizados) es $$$$.
        var premiumStops = stops.Count(s =>
            (s.GetProperty("place").GetProperty("priceRange").GetString() ?? "") == "$$$$");
        Assert.True(premiumStops >= 3,
            $"Con budget=premium se esperaban ≥3 stops $$$$ de {stops.Count}, hubo {premiumStops}");
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static string IsolatedCity(string label) =>
        $"PlanQuality-{label}-{Guid.NewGuid().ToString("N")[..8]}";

    private static HttpResponseMessage GeminiOk(string embeddedText)
    {
        var envelope = new
        {
            candidates = new[]
            {
                new { content = new { parts = new[] { new { text = embeddedText } } } }
            }
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(envelope), Encoding.UTF8, "application/json")
        };
    }

    /// <summary>
    /// Responder de embeddings dirigido para simular una ciudad grande donde la
    /// categoría pedida queda fuera del top-K por cosine: los textos de places
    /// food (nombre "…-food-…") van al eje e2; todo lo demás (query incluida y
    /// places culture) al eje e1. Distancia cosine query↔culture = 0,
    /// query↔food = 1.
    /// </summary>
    private static HttpResponseMessage DirectedEmbeddings(HttpRequestMessage request)
    {
        var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? "{}";
        using var doc = JsonDocument.Parse(body);

        var embeddings = new List<object>();
        if (doc.RootElement.TryGetProperty("requests", out var reqs))
        {
            foreach (var r in reqs.EnumerateArray())
            {
                var text = r.GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? "";
                var values = new float[EmbeddingService.Dimensions];
                if (text.Contains("-food-", StringComparison.OrdinalIgnoreCase))
                    values[1] = 1f;
                else
                    values[0] = 1f;
                embeddings.Add(new { values });
            }
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { embeddings }), Encoding.UTF8, "application/json")
        };
    }

    private async Task SeedPlaces(
        string city, string category, int count,
        string? priceRange = null, string namePrefix = "Place")
    {
        var db = fixture.GetDbContext();
        var tag = Guid.NewGuid().ToString("N")[..8];
        for (int i = 0; i < count; i++)
        {
            db.Places.Add(new Place
            {
                Id = Guid.NewGuid(),
                Name = $"{namePrefix}-{category}-{tag}-{i}",
                Category = category,
                City = city,
                WhyThisPlace = "Plan quality seed",
                Status = "published",
                BestTimes = new List<string> { "any" },
                Latitude = 25.77m + (decimal)(i * 0.01),
                Longitude = -80.19m + (decimal)(i * 0.01),
                GooglePlaceId = $"gpid-pq-{tag}-{i}",
                PriceRange = priceRange,
            });
        }
        await db.SaveChangesAsync();
    }

    private async Task ReindexEmbeddings()
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
        await db.SaveChangesAsync();

        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", fixture.CreateToken(adminFbUid, adminEmail));
        var res = await client.PostAsync("/admin/places/reindex-embeddings", content: null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    private static async Task<List<(string placeId, int day, int order, string arrival)>> ReadStops(
        HttpResponseMessage res)
    {
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("stops").EnumerateArray()
            .Select(s => (
                placeId: s.GetProperty("placeId").GetString() ?? "",
                day: s.GetProperty("dayNumber").GetInt32(),
                order: s.GetProperty("orderIndex").GetInt32(),
                arrival: s.GetProperty("suggestedArrival").GetString() ?? ""))
            .OrderBy(s => s.day).ThenBy(s => s.order)
            .ToList();
    }
}
