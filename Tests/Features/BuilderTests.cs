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
            tripContext = new { city = Miami, days = 1, groupType = "couple" }
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
            tripContext = new { city = Miami, days = 1, groupType = "couple" }
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
            tripContext = new { city = Miami, days = 1, groupType = "couple" }
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
            tripContext = new { city = Miami, days = 1, groupType = "couple" },
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
            message = "some food in Miami for my group",
            tripContext = new { city = Miami, groupType = "couple", days = 1 },
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
                categories = new[] { "outdoors", "culture" }
            }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var planName = body.GetProperty("plan").GetProperty("name").GetString() ?? "";

        Assert.DoesNotContain("Hola", planName, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Miami", planName);
        // Debe venir de categories (outdoors/culture) o duration (2-day).
        Assert.Contains("2-day", planName);
    }

    [Fact]
    public async Task Chat_KeywordFallback_SeedsCategoriesFromWizardCategories()
    {
        // El wizard envía categories directas (food/outdoors/wellness…). En el fallback keyword
        // esas categorías se transfieren directamente a ExtractedPreferences.Categories.
        await SeedPublishedMiamiPlaces(3);

        fixture.FakeGemini.Responder = _ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("", Encoding.UTF8, "text/plain")
        };

        var client = fixture.CreateClient();
        var response = await client.PostAsJsonAsync("/builder/chat", new
        {
            // Mensaje SIN keywords de categories — todo lo que contenga categories tiene que venir del wizard.
            message = "plan",
            tripContext = new
            {
                city = Miami,
                days = 1,
                groupType = "couple",
                categories = new[] { "wellness", "coffee" }
            }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        // La description incluye top categorías: si seed funcionó, contiene "wellness" o "coffee".
        var description = body.GetProperty("plan").GetProperty("description").GetString() ?? "";
        var hasExpectedCategory = description.Contains("wellness", StringComparison.OrdinalIgnoreCase)
                               || description.Contains("coffee", StringComparison.OrdinalIgnoreCase);
        Assert.True(hasExpectedCategory,
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

    // ── Fix 2026-04-23: context wins — Gemini devolvía vacío/default ignorando el prompt ───

    [Fact]
    public async Task Chat_GeminiReturnsEmpty_ContextWins_RespectsFamilyAndDays()
    {
        // Reproduce el bug observado en prod: Gemini responde con campos vacíos/default
        // (categories=[], vibes=[], groupType="", planName="My Plan", days=1) y el
        // pipeline histórico caía a defaults "couple + 1 día + nightlife permitido".
        // MergeContextIntoPrefs fuerza los valores del wizard sobre los de Gemini.
        await SeedPlace("FamilyCoffee", "coffee", "morning", new List<string> { "family" });
        await SeedPlace("FamilyCulture", "culture", "afternoon", new List<string> { "family" });
        await SeedPlace("FamilyFood", "food", "lunch", new List<string> { "family" });
        await SeedPlace("Bar", "nightlife", "evening", new List<string> { "adults-only" });

        // Gemini responde vacío — imita el comportamiento real en prod.
        var empty = new
        {
            days = 1,
            categories = new string[] { },
            vibes = new string[] { },
            groupType = "",
            planName = "My Plan",
            maxStopsPerDay = 5,
        };
        fixture.FakeGemini.Responder = _ => GeminiOk(JsonSerializer.Serialize(empty));

        var client = fixture.CreateClient();
        var response = await client.PostAsJsonAsync("/builder/chat", new
        {
            message = "x",
            tripContext = new { city = Miami, groupType = "family-kids", categories = new[] { "outdoors", "culture" }, days = 3 },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Context wins: days del wizard, no el 1 por default de Gemini.
        Assert.Equal(3, body.GetProperty("plan").GetProperty("durationDays").GetInt32());

        // Name no es el placeholder "My Plan".
        var planName = body.GetProperty("plan").GetProperty("name").GetString() ?? "";
        Assert.NotEqual("My Plan", planName);

        // Family-kids: 0 stops de nightlife (hard exclusion via merge + matrix).
        foreach (var s in body.GetProperty("stops").EnumerateArray())
        {
            var cat = s.GetProperty("place").GetProperty("category").GetString() ?? "";
            Assert.False(
                string.Equals(cat, "nightlife", StringComparison.OrdinalIgnoreCase),
                $"family-kids con Gemini vacío no debería traer nightlife, llegó {cat}");
        }
    }

    [Fact]
    public async Task Chat_GeminiReturnsConflictingGroupType_ContextWins()
    {
        // Gemini devuelve groupType="solo" contradiciendo el wizard que envió "family-kids".
        // MergeContextIntoPrefs sobreescribe con el valor del wizard.
        await SeedPlace("FamilyCafe", "coffee", "morning", new List<string> { "family" });
        await SeedPlace("FamilyPark", "outdoors", "afternoon", new List<string> { "family", "kids" });
        await SeedPlace("AdultBar", "nightlife", "evening", new List<string> { "adults-only" });

        var conflict = new
        {
            days = 2,
            categories = new[] { "nightlife", "food" },
            vibes = new[] { "party" },
            groupType = "solo",  // ← contradice el wizard
            planName = "Solo Miami Night",
            maxStopsPerDay = 5,
        };
        fixture.FakeGemini.Responder = _ => GeminiOk(JsonSerializer.Serialize(conflict));

        var client = fixture.CreateClient();
        var response = await client.PostAsJsonAsync("/builder/chat", new
        {
            message = "family day out",
            tripContext = new { city = Miami, groupType = "family-kids", categories = new[] { "culture" }, days = 1 },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Days del wizard (1), no el 2 de Gemini.
        Assert.Equal(1, body.GetProperty("plan").GetProperty("durationDays").GetInt32());

        // 0 nightlife en stops aunque Gemini lo incluía en categories.
        foreach (var s in body.GetProperty("stops").EnumerateArray())
        {
            var cat = s.GetProperty("place").GetProperty("category").GetString() ?? "";
            Assert.NotEqual("nightlife", cat);
        }
    }

    [Fact]
    public async Task Chat_NoContext_GeminiPrevailsForGroupType()
    {
        // Sin tripContext (o con context vacío), el pipeline debe honrar lo que diga Gemini.
        // Verifica que MergeContextIntoPrefs no rompe el comportamiento cuando no hay context.
        await SeedPlace("SoloFood", "food", "lunch");
        await SeedPlace("SoloCoffee", "coffee", "morning");

        var extracted = new
        {
            days = 1,
            categories = new[] { "food", "coffee" },
            vibes = new[] { "casual" },
            groupType = "friends",
            planName = "Friends Miami Day",
            maxStopsPerDay = 4,
        };
        fixture.FakeGemini.Responder = _ => GeminiOk(JsonSerializer.Serialize(extracted));

        var client = fixture.CreateClient();
        var response = await client.PostAsJsonAsync("/builder/chat", new
        {
            message = "weekend trip with my friends in Miami exploring food spots",
            // Con tripContext con 3 señales wizard para pasar validación.
            tripContext = new { city = Miami, days = 1, groupType = "friends" },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        // planName pasa through (no es placeholder ni greeting ni match raw).
        Assert.Equal("Friends Miami Day", body.GetProperty("plan").GetProperty("name").GetString());
    }

    // ── Input validation (Pablo feedback 2026-04-23) ───────────────────────

    [Fact]
    public async Task Chat_TrivialMessageNoContext_Returns400_InsufficientInput()
    {
        var client = fixture.CreateClient();
        var response = await client.PostAsJsonAsync("/builder/chat", new
        {
            message = "x",
            // Sin tripContext → 0 señales wizard + mensaje no descriptivo → 400.
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("insufficient_input", body.GetProperty("error").GetString());
        // chat_message=true (hay mensaje "x") pero ninguna señal wizard → <3 → 400.
        Assert.True(body.GetProperty("signals").GetProperty("chat_message").GetBoolean());
        Assert.False(body.GetProperty("signals").GetProperty("wizard_days").GetBoolean());
        Assert.False(body.GetProperty("signals").GetProperty("wizard_city").GetBoolean());
        Assert.False(body.GetProperty("signals").GetProperty("wizard_groupType").GetBoolean());
    }

    [Fact]
    public async Task Chat_GreetingLongButNoContext_Returns400_InsufficientInput()
    {
        var client = fixture.CreateClient();
        var response = await client.PostAsJsonAsync("/builder/chat", new
        {
            // Long pero empieza con greeting → no cuenta como descriptivo.
            message = "hola que tal amigo como va todo por miami hoy",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("insufficient_input", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Chat_OneWizardSignal_TrivialMessage_Returns400_InsufficientInput()
    {
        var client = fixture.CreateClient();
        var response = await client.PostAsJsonAsync("/builder/chat", new
        {
            message = "x",
            tripContext = new { city = Miami, groupType = "couple" },  // solo 1 señal wizard
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("signals").GetProperty("wizard_groupType").GetBoolean());
        Assert.False(body.GetProperty("signals").GetProperty("wizard_days").GetBoolean());
        Assert.False(body.GetProperty("signals").GetProperty("wizard_interests").GetBoolean());
    }

    [Fact]
    public async Task Chat_TwoWizardSignals_TrivialMessage_Returns200()
    {
        await SeedPublishedMiamiPlaces(3);

        var extracted = new
        {
            days = 1,
            categories = new[] { "food" },
            vibes = new string[] { },
            groupType = "couple",
            planName = "Test",
            maxStopsPerDay = 4,
        };
        fixture.FakeGemini.Responder = _ => GeminiOk(JsonSerializer.Serialize(extracted));

        var client = fixture.CreateClient();
        var response = await client.PostAsJsonAsync("/builder/chat", new
        {
            message = "x",
            tripContext = new { city = Miami, groupType = "couple", days = 2 },  // 2 señales
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Chat_DescriptiveMessageAlone_Returns400_WizardRequired()
    {
        // Regla Pablo 2026-04-23: el chat NO sustituye al wizard, aunque sea descriptivo.
        // Siempre se requieren ≥3 señales wizard. Solo mensaje → 400.
        var client = fixture.CreateClient();
        var response = await client.PostAsJsonAsync("/builder/chat", new
        {
            message = "romantic dinner in Wynwood with my wife",  // 40 chars pero sin wizard
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("insufficient_input", body.GetProperty("error").GetString());
        // chat_message es true pero no basta sin wizard.
        Assert.True(body.GetProperty("signals").GetProperty("chat_message").GetBoolean());
    }

    [Fact]
    public async Task Chat_NoMessage_ThreeWizardSignals_Returns200()
    {
        // Regla Pablo: el chat es opcional. Con solo 3 señales wizard, plan se genera.
        await SeedPublishedMiamiPlaces(3);

        var extracted = new
        {
            days = 1,
            categories = new[] { "food" },
            vibes = new string[] { },
            groupType = "couple",
            planName = "Test",
            maxStopsPerDay = 4,
        };
        fixture.FakeGemini.Responder = _ => GeminiOk(JsonSerializer.Serialize(extracted));

        var client = fixture.CreateClient();
        var response = await client.PostAsJsonAsync("/builder/chat", new
        {
            // Message omitido — es opcional.
            tripContext = new { city = Miami, days = 1, groupType = "couple" },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Chat_WizardSendsCategoriesNoPreferences_CountsAsSignal()
    {
        // Verifica que el wizard nuevo (categories en lugar de preferences) cuenta como señal.
        // city + days + groupType + categories = 4 señales → ≥3 → 200.
        await SeedPublishedMiamiPlaces(3);

        var extracted = new
        {
            days = 1,
            categories = new[] { "food" },
            vibes = new string[] { },
            groupType = "couple",
            planName = "Test",
            maxStopsPerDay = 4,
        };
        fixture.FakeGemini.Responder = _ => GeminiOk(JsonSerializer.Serialize(extracted));

        var client = fixture.CreateClient();
        var response = await client.PostAsJsonAsync("/builder/chat", new
        {
            tripContext = new { city = Miami, days = 1, groupType = "couple", categories = new[] { "food" } },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("plan", out _));
    }

    [Fact]
    public async Task Chat_NoCategoriesNoInterests_BelowThreshold_Returns400()
    {
        // Sin categories ni subcategories, city + groupType = 2 señales → <3 → 400.
        var client = fixture.CreateClient();
        var response = await client.PostAsJsonAsync("/builder/chat", new
        {
            tripContext = new { city = Miami, groupType = "couple" },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("insufficient_input", body.GetProperty("error").GetString());
        Assert.False(body.GetProperty("signals").GetProperty("wizard_interests").GetBoolean());
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
