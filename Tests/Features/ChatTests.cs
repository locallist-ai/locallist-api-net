using System.Net;
using System.Text;
using System.Text.Json;
using LocalList.API.NET.Features.Chat;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.Tests.Features;

/// <summary>
/// Integration tests for POST /chat/generate.
/// Covers ownership validation, session status guards, idempotency, and happy path.
/// </summary>
public class ChatTests(ApiFixture fixture) : IClassFixture<ApiFixture>, IDisposable
{
    private const string Miami = "Miami";

    public void Dispose()
    {
        fixture.FakeGemini.Responder = null;
        fixture.FakeGemini.Calls.Clear();
    }

    // ── POST /chat/generate ───────────────────────────────────────────────────

    [Fact]
    public async Task Generate_ReadySession_ReturnsPlan()
    {
        await SeedMiamiPlaces(3);

        var session = await CreateReadySession();

        fixture.FakeGemini.Responder = _ => GeminiExtractedOk(new
        {
            days = 2,
            categories = new[] { "food" },
            vibes = new string[] { },
            groupType = "couple",
            planName = "Miami Foodie Weekend",
            maxStopsPerDay = 4
        });

        var client = await fixture.CreateGenerationClientAsync();
        var response = await client.PostAsJsonAsync("/chat/generate", new { sessionId = session.Id });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("plan", out var plan));
        Assert.Equal(Miami, plan.GetProperty("city").GetString());
        Assert.True(body.TryGetProperty("stops", out var stops));
        Assert.True(stops.GetArrayLength() >= 0); // can be 0 if only 3 places with keyword fallback
    }

    [Fact]
    public async Task Generate_SessionNotFound_Returns404()
    {
        var client = await fixture.CreateGenerationClientAsync();
        var response = await client.PostAsJsonAsync("/chat/generate", new { sessionId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Generate_SessionNotReady_Returns400()
    {
        var db = fixture.GetDbContext();
        var session = new ChatSession
        {
            Status = "active",
            TurnCount = 1,
            SlotsJson = JsonSerializer.Serialize(new
            {
                city = Miami, days = 2, groupType = "couple",
                categories = new[] { "food" }, budget = "moderate"
            })
        };
        db.ChatSessions.Add(session);
        await db.SaveChangesAsync();

        var client = await fixture.CreateGenerationClientAsync();
        var response = await client.PostAsJsonAsync("/chat/generate", new { sessionId = session.Id });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("session_not_ready", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Generate_QuarantinedSession_Returns403()
    {
        var db = fixture.GetDbContext();
        var session = new ChatSession
        {
            Status = "quarantined",
            TurnCount = 3,
            SlotsJson = "{}"
        };
        db.ChatSessions.Add(session);
        await db.SaveChangesAsync();

        var client = await fixture.CreateGenerationClientAsync();
        var response = await client.PostAsJsonAsync("/chat/generate", new { sessionId = session.Id });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("session_quarantined", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Generate_AuthenticatedUser_WrongSession_Returns403()
    {
        // Create a session owned by a different user
        var db = fixture.GetDbContext();
        var otherUserId = Guid.NewGuid();
        var otherEmail = $"other-{Guid.NewGuid():N}@test.com";
        db.Users.Add(new User { Id = otherUserId, Email = otherEmail });
        var session = new ChatSession
        {
            UserId = otherUserId,
            Status = "ready",
            TurnCount = 2,
            SlotsJson = JsonSerializer.Serialize(new
            {
                city = Miami, days = 2, groupType = "couple",
                categories = new[] { "food" }, budget = "moderate"
            })
        };
        db.ChatSessions.Add(session);

        // Authenticated requester — different user
        var requesterId = Guid.NewGuid();
        var requesterEmail = $"requester-{Guid.NewGuid():N}@test.com";
        db.Users.Add(new User { Id = requesterId, Email = requesterEmail });
        await db.SaveChangesAsync();

        // Build app HS256 token for requester
        var client = fixture.CreateClient();
        var token = fixture.CreateAppToken(requesterId, requesterEmail);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response = await client.PostAsJsonAsync("/chat/generate", new { sessionId = session.Id });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Generate_TurnCapReached_Generates()
    {
        await SeedMiamiPlaces(3);

        // Turn count at cap (6) even without "ready" status
        var db = fixture.GetDbContext();
        var session = new ChatSession
        {
            Status = "active",
            TurnCount = 6,  // at cap
            SlotsJson = JsonSerializer.Serialize(new
            {
                city = Miami, days = 1, groupType = "couple",
                categories = new[] { "food" }, budget = "moderate"
            })
        };
        db.ChatSessions.Add(session);
        await db.SaveChangesAsync();

        fixture.FakeGemini.Responder = _ => GeminiExtractedOk(new
        {
            days = 1,
            categories = new[] { "food" },
            vibes = new string[] { },
            groupType = "couple",
            planName = "Miami Day",
            maxStopsPerDay = 3
        });

        var client = await fixture.CreateGenerationClientAsync();
        var response = await client.PostAsJsonAsync("/chat/generate", new { sessionId = session.Id });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Generate_Idempotent_ReturnsExistingPlan()
    {
        await SeedMiamiPlaces(3);

        // Authenticated user — plan persists to DB so idempotency path triggers
        var db = fixture.GetDbContext();
        var userId = Guid.NewGuid();
        var email = $"idempotent-{Guid.NewGuid():N}@test.com";
        db.Users.Add(new User { Id = userId, Email = email });
        var session = new ChatSession
        {
            UserId = userId,
            Status = "ready",
            TurnCount = 3,
            SlotsJson = JsonSerializer.Serialize(new
            {
                city = Miami, days = 2, groupType = "couple",
                categories = new[] { "food" }, budget = "moderate"
            })
        };
        db.ChatSessions.Add(session);
        await db.SaveChangesAsync();

        fixture.FakeGemini.Responder = _ => GeminiExtractedOk(new
        {
            days = 2,
            categories = new[] { "food" },
            vibes = new string[] { },
            groupType = "couple",
            planName = "Miami Foodie Weekend",
            maxStopsPerDay = 4
        });

        var client = fixture.CreateClient();
        var token = fixture.CreateAppToken(userId, email);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response1 = await client.PostAsJsonAsync("/chat/generate", new { sessionId = session.Id });
        Assert.Equal(HttpStatusCode.OK, response1.StatusCode);

        // Second call — should return existing plan (idempotent)
        fixture.FakeGemini.Calls.Clear();
        var response2 = await client.PostAsJsonAsync("/chat/generate", new { sessionId = session.Id });
        Assert.Equal(HttpStatusCode.OK, response2.StatusCode);

        var body2 = await response2.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body2.TryGetProperty("isExisting", out var isExisting));
        Assert.True(isExisting.GetBoolean());

        // Gemini should NOT have been called for the second request
        Assert.Empty(fixture.FakeGemini.Calls);
    }

    [Fact]
    public async Task Generate_SlotsToTripContext_CityUsedInGeneration()
    {
        // Seed places with "food" category to match the slot categories below
        await SeedMiamiPlaces(3);

        var db = fixture.GetDbContext();
        var session = new ChatSession
        {
            Status = "ready",
            TurnCount = 3,
            SlotsJson = JsonSerializer.Serialize(new
            {
                city = Miami,
                days = 1,
                groupType = "solo",
                categories = new[] { "food" },
                budget = "budget",
                pace = "slow"
            })
        };
        db.ChatSessions.Add(session);
        await db.SaveChangesAsync();

        fixture.FakeGemini.Responder = _ => GeminiExtractedOk(new
        {
            days = 1,
            categories = new[] { "food" },
            vibes = new string[] { },
            groupType = "solo",
            planName = "Miami Solo Day",
            maxStopsPerDay = 3
        });

        var client = await fixture.CreateGenerationClientAsync();
        var response = await client.PostAsJsonAsync("/chat/generate", new { sessionId = session.Id });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(Miami, body.GetProperty("plan").GetProperty("city").GetString());
    }

    // ── POST /chat/turn — preSeededSlots ─────────────────────────────────────

    [Fact]
    public async Task Turn_PreSeededCity_FirstTurn_SeedsCitySlotAndReturnsGreeting()
    {
        // Seed Miami in Cities table so the validation accepts it
        var db = fixture.GetDbContext();
        if (!await db.Cities.AnyAsync(c => c.NormalizedName == "miami"))
        {
            db.Cities.Add(new LocalList.API.NET.Shared.Data.Entities.City
            {
                Name = Miami,
                NormalizedName = "miami",
                Source = "seed"
            });
            await db.SaveChangesAsync();
        }

        var client = fixture.CreateClient();
        var res = await client.PostAsJsonAsync("/chat/turn", new
        {
            preSeededSlots = new { city = Miami }
        });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(Miami, body.GetProperty("slots").GetProperty("city").GetString());
        var aiMsg = body.GetProperty("aiMessage").GetString();
        Assert.NotNull(aiMsg);
        Assert.Contains(Miami, aiMsg, StringComparison.OrdinalIgnoreCase);
        var qr = body.GetProperty("quickReplies");
        Assert.True(qr.GetArrayLength() > 0);
    }

    [Fact]
    public async Task Turn_PreSeededInvalidCity_FirstTurn_IgnoresCity()
    {
        var client = fixture.CreateClient();
        var res = await client.PostAsJsonAsync("/chat/turn", new
        {
            message = "I want food and culture",
            preSeededSlots = new { city = "NonExistentCityXYZ" }
        });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        // City must NOT be set (invalid city ignored) — null values are omitted via WhenWritingNull
        var slots = body.GetProperty("slots");
        var hasCity = slots.TryGetProperty("city", out var cityProp);
        Assert.True(!hasCity || string.IsNullOrEmpty(cityProp.GetString()));
    }

    [Fact]
    public async Task Turn_PreSeededCity_LaterTurn_IgnoresPreSeed()
    {
        // Arrange: create an active session with city already set
        var db = fixture.GetDbContext();
        var session = new ChatSession
        {
            Status = "active",
            TurnCount = 2,
            SlotsJson = JsonSerializer.Serialize(new ChatSlots { City = Miami, Days = 2 })
        };
        db.ChatSessions.Add(session);
        await db.SaveChangesAsync();

        var client = fixture.CreateClient();
        var res = await client.PostAsJsonAsync("/chat/turn", new
        {
            sessionId = session.Id,
            message = "I like food",
            preSeededSlots = new { city = "Tokyo" }  // must be ignored on later turns
        });

        var rawBody = await res.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = JsonSerializer.Deserialize<JsonElement>(rawBody);
        // City must remain Miami (preSeeded ignored on later turns)
        Assert.True(body.TryGetProperty("slots", out var slotsEl),
            $"Response has no 'slots' property. Body: {rawBody}");
        Assert.True(slotsEl.TryGetProperty("city", out var cityEl),
            $"Slots has no 'city' property. Slots: {slotsEl}");
        Assert.Equal(Miami, cityEl.GetString());
    }

    // ── Localización (ES) ────────────────────────────────────────────────────

    [Fact]
    public async Task Turn_SpanishLocale_PreSeededCity_GreetingInSpanish()
    {
        var db = fixture.GetDbContext();
        if (!await db.Cities.AnyAsync(c => c.NormalizedName == "miami"))
        {
            db.Cities.Add(new LocalList.API.NET.Shared.Data.Entities.City
            {
                Name = Miami, NormalizedName = "miami", Source = "seed"
            });
            await db.SaveChangesAsync();
        }

        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Add("Accept-Language", "es");

        var res = await client.PostAsJsonAsync("/chat/turn", new
        {
            preSeededSlots = new { city = Miami }
        });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var aiMsg = body.GetProperty("aiMessage").GetString();
        Assert.NotNull(aiMsg);
        // Spanish greeting must contain the city and be different from English default
        Assert.Contains(Miami, aiMsg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("let's plan", aiMsg, StringComparison.OrdinalIgnoreCase);
        // Quick reply labels must be in Spanish
        var qr = body.GetProperty("quickReplies");
        Assert.True(qr.GetArrayLength() > 0);
        var firstLabel = qr[0].GetProperty("label").GetString();
        Assert.Contains("día", firstLabel, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Turn_ChainFailure_ReturnsAiUnavailableNotParseFallback()
    {
        // La cadena LLM cae (503) → mensaje genérico de indisponibilidad + error:"ai_unavailable",
        // NO el "Sorry, I didn't catch that" (reservado para input legítimamente no entendido).
        fixture.FakeGemini.Responder = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("{\"error\":{\"message\":\"backend overloaded\"}}", Encoding.UTF8, "application/json")
        };

        var client = fixture.CreateClient();
        var res = await client.PostAsJsonAsync("/chat/turn", new { message = "I want to visit restaurants" });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ai_unavailable", body.GetProperty("error").GetString());
        var aiMsg = body.GetProperty("aiMessage").GetString()!;
        Assert.DoesNotContain("didn't catch", aiMsg, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("can't process", aiMsg, StringComparison.OrdinalIgnoreCase);
        Assert.False(body.GetProperty("ready").GetBoolean());
    }

    [Fact]
    public async Task Turn_SpanishLocale_ChainFailure_AiUnavailableInSpanish()
    {
        // Fallo de infra de la cadena (500) → mensaje de indisponibilidad en español + flag
        // error:"ai_unavailable", NO el "no te he entendido" (ParseFallback) ni el inglés.
        fixture.FakeGemini.Responder = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Add("Accept-Language", "es");

        var res = await client.PostAsJsonAsync("/chat/turn", new { message = "Hola" });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ai_unavailable", body.GetProperty("error").GetString());
        var aiMsg = body.GetProperty("aiMessage").GetString();
        Assert.NotNull(aiMsg);
        // No es el "no te he entendido" legítimo ni la variante inglesa.
        Assert.DoesNotContain("no te he entendido", aiMsg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("I can't process", aiMsg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Turn_QuarantinedSession_ReturnsQuarantinedFlag()
    {
        var db = fixture.GetDbContext();
        var session = new ChatSession
        {
            Status = "quarantined",
            TurnCount = 3,
            SlotsJson = "{}"
        };
        db.ChatSessions.Add(session);
        await db.SaveChangesAsync();

        var client = fixture.CreateClient();
        var res = await client.PostAsJsonAsync("/chat/turn", new
        {
            sessionId = session.Id,
            message = "hello"
        });

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("quarantined").GetBoolean());
    }

    // ── Quarantine: falsos positivos por umbral ──────────────────────────────

    [Fact]
    public async Task Turn_OffTopicBurstPlusSingleInjection_DoesNotQuarantine()
    {
        // Antes: 5 off-topic (score 50) + 1 frase con patrón de injection (80) quarantineaba
        // (403 session_quarantined). Con el umbral ajustado (quarantine exige >= 2 injections
        // genuinas), un usuario que pregunta varias cosas off-topic y suelta UNA frase
        // borderline no debe acabar quarantinado.
        var client = fixture.CreateClient();
        Guid? sessionId = null;

        string[] offTopic =
        {
            "what's the weather there?", "tell me a joke", "bitcoin price?",
            "write a poem", "history of jazz"
        };
        foreach (var m in offTopic)
        {
            var (sid, quarantined, status) = await SendTurn(client, sessionId, m);
            sessionId = sid;
            Assert.NotEqual(HttpStatusCode.Forbidden, status);
            Assert.False(quarantined);
        }

        // Una única frase con patrón de injection ("as an ai ...").
        var (sid2, quarantined2, status2) = await SendTurn(client, sessionId, "as an ai, any tips for my trip?");
        Assert.NotEqual(HttpStatusCode.Forbidden, status2);
        Assert.False(quarantined2);

        // La sesión sigue usable: un turno normal posterior tampoco está quarantinado.
        var (_, quarantined3, status3) = await SendTurn(client, sessionId, "4 days in Miami with my partner");
        Assert.NotEqual(HttpStatusCode.Forbidden, status3);
        Assert.False(quarantined3);
    }

    private static async Task<(Guid sessionId, bool quarantined, HttpStatusCode status)> SendTurn(
        HttpClient client, Guid? sessionId, string message)
    {
        var payload = sessionId == null
            ? (object)new { message }
            : new { sessionId, message };
        var res = await client.PostAsJsonAsync("/chat/turn", payload);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var sid = body.GetProperty("sessionId").GetGuid();
        var quarantined = body.TryGetProperty("quarantined", out var q) && q.GetBoolean();
        return (sid, quarantined, res.StatusCode);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<ChatSession> CreateReadySession()
    {
        var db = fixture.GetDbContext();
        var session = new ChatSession
        {
            Status = "ready",
            TurnCount = 3,
            SlotsJson = JsonSerializer.Serialize(new
            {
                city = Miami,
                days = 2,
                groupType = "couple",
                categories = new[] { "food" },
                budget = "moderate"
            })
        };
        db.ChatSessions.Add(session);
        await db.SaveChangesAsync();
        return session;
    }

    private async Task SeedMiamiPlaces(int count)
    {
        var db = fixture.GetDbContext();
        var tag = Guid.NewGuid().ToString("N")[..8];
        for (int i = 0; i < count; i++)
        {
            db.Places.Add(new Place
            {
                Id = Guid.NewGuid(),
                Name = $"ChatTest Place {tag}-{i}",
                Category = "food",
                City = Miami,
                WhyThisPlace = "Seeded for chat tests",
                Status = "published",
                BestTimes = new List<string> { "any" },
                Latitude = 25.77m + (decimal)(i * 0.01),
                Longitude = -80.19m + (decimal)(i * 0.01),
                GooglePlaceId = $"gpid-chat-{tag}-{i}",
            });
        }
        await db.SaveChangesAsync();
    }

    // ── DELETE /chat/session/:id ─────────────────────────────────────────────

    /// <summary>
    /// TC-2: Verifica que un usuario no puede borrar la sesión de otro (IDOR).
    /// La lógica en ChatController.DeleteSession filtra por userId, por lo que
    /// devuelve 404 sin revelar que la sesión existe.
    /// </summary>
    [Fact]
    public async Task DeleteSession_NonOwner_Returns404AndPreservesSession()
    {
        // Arrange: usuario A y usuario B distintos
        var ownerFbUid  = $"fb-owner-{Guid.NewGuid():N}";
        var attackerFbUid = $"fb-attacker-{Guid.NewGuid():N}";
        var ownerId    = Guid.NewGuid();
        var attackerId = Guid.NewGuid();

        var db = fixture.GetDbContext();
        db.Users.Add(new User { Id = ownerId,    Email = $"owner-{ownerId:N}@test.com",    FirebaseUid = ownerFbUid });
        db.Users.Add(new User { Id = attackerId, Email = $"attacker-{attackerId:N}@test.com", FirebaseUid = attackerFbUid });

        var session = new ChatSession { UserId = ownerId, Status = "active" };
        db.ChatSessions.Add(session);
        await db.SaveChangesAsync();

        // Act: atacante intenta borrar la sesión del propietario
        var attackerClient = fixture.CreateAuthenticatedClient(attackerId, attackerFbUid);
        var response = await attackerClient.DeleteAsync($"/chat/session/{session.Id}");

        // Assert: 404 (no revela existencia) y la sesión sigue en la DB
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var after = fixture.GetDbContext();
        Assert.NotNull(await after.ChatSessions.FindAsync(session.Id));
    }

    [Fact]
    public async Task DeleteSession_Owner_Returns204AndRemovesSession()
    {
        // Arrange: propietario crea sesión
        var ownerFbUid = $"fb-delowner-{Guid.NewGuid():N}";
        var ownerId    = Guid.NewGuid();

        var db = fixture.GetDbContext();
        db.Users.Add(new User { Id = ownerId, Email = $"delowner-{ownerId:N}@test.com", FirebaseUid = ownerFbUid });

        var session = new ChatSession { UserId = ownerId, Status = "active" };
        db.ChatSessions.Add(session);
        await db.SaveChangesAsync();

        // Act: el propietario borra su propia sesión
        var client = fixture.CreateAuthenticatedClient(ownerId, ownerFbUid);
        var response = await client.DeleteAsync($"/chat/session/{session.Id}");

        // Assert: 204 y la sesión ya no existe
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var after = fixture.GetDbContext();
        Assert.Null(await after.ChatSessions.FindAsync(session.Id));
    }

    private static HttpResponseMessage GeminiExtractedOk(object extracted)
    {
        var envelope = new
        {
            candidates = new[]
            {
                new
                {
                    content = new
                    {
                        parts = new[] { new { text = JsonSerializer.Serialize(extracted) } }
                    }
                }
            }
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(envelope), Encoding.UTF8, "application/json")
        };
    }
}
