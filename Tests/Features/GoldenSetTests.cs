using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.Tests.Features;

/// <summary>
/// Quality-gate tests for the builder and chat pipelines.
///
/// Each test covers a canonical end-to-end scenario that the mobile app relies on.
/// These act as regression guards: if a core pipeline change breaks a golden-set
/// expectation, these tests catch it before it reaches production.
/// </summary>
public class GoldenSetTests(ApiFixture fixture) : IClassFixture<ApiFixture>, IDisposable
{
    private const string Miami = "Miami";

    public void Dispose()
    {
        fixture.FakeGemini.Responder = null;
        fixture.FakeGemini.Calls.Clear();
    }

    // ── Golden Set 1: Multi-day plan distributes stops across all days ────────

    [Fact]
    public async Task Builder_ThreeDayPlan_StopsDistributedAcrossAllDays()
    {
        // 9 places → 3-day request → scheduler must populate days 1, 2, and 3.
        await SeedMiamiPlaces(9, "Food");

        fixture.FakeGemini.Responder = _ => GeminiOk(JsonSerializer.Serialize(new
        {
            days = 3,
            categories = new[] { "food" },
            vibes = new string[] { },
            groupType = "couple",
            planName = "Miami 3-Day Foodie",
            maxStopsPerDay = 3
        }));

        var client = await fixture.CreateGenerationClientAsync();
        var res = await client.PostAsJsonAsync("/builder/chat", new
        {
            message = "3 días en Miami comida",
            tripContext = new { city = Miami, days = 3, groupType = "couple", categories = new[] { "food" } }
        });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        var plan = body.GetProperty("plan");
        Assert.Equal(3, plan.GetProperty("durationDays").GetInt32());

        var stops = body.GetProperty("stops").EnumerateArray().ToList();
        Assert.True(stops.Count >= 3, $"Expected >= 3 stops, got {stops.Count}");

        var days = stops.Select(s => s.GetProperty("dayNumber").GetInt32()).Distinct().OrderBy(x => x).ToList();
        Assert.Contains(1, days);
        Assert.Contains(2, days);
        Assert.Contains(3, days);
    }

    // ── Golden Set 2: Builder mobile contract shape ───────────────────────────

    [Fact]
    public async Task Builder_Authenticated_PlanShape_MatchesMobileContract()
    {
        // F4: /builder/chat exige auth (el flujo anónimo devolvía plan efímero; ya no existe).
        // The response must return all fields the mobile app reads:
        // plan.id, plan.name, plan.city, plan.type, plan.durationDays (persisted, NOT ephemeral)
        // stops[i].placeId, stops[i].dayNumber, stops[i].orderIndex
        await SeedMiamiPlaces(3, "Food");

        fixture.FakeGemini.Responder = _ => GeminiOk(JsonSerializer.Serialize(new
        {
            days = 1,
            categories = new[] { "food" },
            vibes = new string[] { },
            groupType = "solo",
            planName = "Miami Solo Day",
            maxStopsPerDay = 3
        }));

        var client = await fixture.CreateGenerationClientAsync();
        var res = await client.PostAsJsonAsync("/builder/chat", new
        {
            message = "día en miami solo",
            tripContext = new { city = Miami, days = 1, groupType = "solo", categories = new[] { "food" } }
        });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        // Plan-level contract
        var plan = body.GetProperty("plan");
        Assert.True(plan.TryGetProperty("id", out _), "plan.id missing");
        Assert.True(plan.TryGetProperty("name", out var planName), "plan.name missing");
        Assert.False(string.IsNullOrEmpty(planName.GetString()), "plan.name is empty");
        Assert.Equal(Miami, plan.GetProperty("city").GetString());
        Assert.Equal("ai", plan.GetProperty("type").GetString());
        Assert.True(plan.TryGetProperty("durationDays", out _), "plan.durationDays missing");
        // F4: la generación autenticada SIEMPRE persiste — nunca ephemeral.
        Assert.False(plan.TryGetProperty("isEphemeral", out var eph) && eph.GetBoolean(),
            "authenticated plan must not be ephemeral");

        // Stops contract
        var stops = body.GetProperty("stops").EnumerateArray().ToList();
        foreach (var stop in stops)
        {
            Assert.True(stop.TryGetProperty("placeId", out _), "stop.placeId missing");
            Assert.True(stop.TryGetProperty("dayNumber", out _), "stop.dayNumber missing");
            Assert.True(stop.TryGetProperty("orderIndex", out _), "stop.orderIndex missing");
            Assert.True(stop.TryGetProperty("place", out var place), "stop.place missing");
            Assert.True(place.TryGetProperty("id", out _), "stop.place.id missing");
            Assert.True(place.TryGetProperty("name", out _), "stop.place.name missing");
        }

        // Response-level contract
        Assert.True(body.TryGetProperty("message", out _), "message field missing");
        Assert.True(body.TryGetProperty("warnings", out _), "warnings field missing");
        Assert.True(body.TryGetProperty("appliedRefinements", out _), "appliedRefinements field missing");
    }

    // ── Golden Set 3: Authenticated builder persists plan + analytics ─────────

    [Fact]
    public async Task Builder_Authenticated_PersistsPlanAndPlanMetric()
    {
        var (client, userId) = CreateAuthClient();
        await SeedMiamiPlaces(3, "Food");

        fixture.FakeGemini.Responder = _ => GeminiOk(JsonSerializer.Serialize(new
        {
            days = 1,
            categories = new[] { "food" },
            vibes = new[] { "romantic" },
            groupType = "couple",
            planName = "Miami Romantic Dinner",
            maxStopsPerDay = 3
        }));

        var res = await client.PostAsJsonAsync("/builder/chat", new
        {
            message = "cena romántica miami",
            tripContext = new { city = Miami, days = 1, groupType = "couple", categories = new[] { "food" } }
        });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        // Plan is persisted (not ephemeral)
        var plan = body.GetProperty("plan");
        Assert.False(plan.TryGetProperty("isEphemeral", out var eph) && eph.GetBoolean(),
            "authenticated plan must not be ephemeral");

        var planId = Guid.Parse(plan.GetProperty("id").GetString()!);

        var db = fixture.GetDbContext();
        var dbPlan = await db.Plans.FindAsync(planId);
        Assert.NotNull(dbPlan);
        Assert.Equal(Miami, dbPlan!.City);
        Assert.Equal(userId, dbPlan.CreatedById);

        // PlanMetric row created
        var metric = db.PlanMetrics.FirstOrDefault(m => m.PlanId == planId);
        Assert.NotNull(metric);
        Assert.Equal("builder", metric!.GenerationSource);
        Assert.True(metric.NumStops >= 0);
        Assert.True(metric.LatencyMs >= 0);
    }

    // ── Golden Set 4: Chat generate persists plan + analytics ────────────────

    [Fact]
    public async Task Chat_Generate_Authenticated_PersistsPlanAndPlanMetric()
    {
        var (client, userId) = CreateAuthClient();
        await SeedMiamiPlaces(4, "Food");

        var db = fixture.GetDbContext();
        var session = new ChatSession
        {
            Status = "ready",
            TurnCount = 3,
            SlotsJson = JsonSerializer.Serialize(new
            {
                city = Miami, days = 2, groupType = "friends",
                categories = new[] { "food" }, budget = "moderate"
            })
        };
        db.ChatSessions.Add(session);
        await db.SaveChangesAsync();

        fixture.FakeGemini.Responder = _ => GeminiOk(JsonSerializer.Serialize(new
        {
            days = 2,
            categories = new[] { "food" },
            vibes = new string[] { },
            groupType = "friends",
            planName = "Miami Friends Weekend",
            maxStopsPerDay = 3
        }));

        var res = await client.PostAsJsonAsync("/chat/generate", new { sessionId = session.Id });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var plan = body.GetProperty("plan");
        var planId = Guid.Parse(plan.GetProperty("id").GetString()!);

        var freshDb = fixture.GetDbContext();
        var dbPlan = await freshDb.Plans.FindAsync(planId);
        Assert.NotNull(dbPlan);

        var metric = freshDb.PlanMetrics.FirstOrDefault(m => m.PlanId == planId);
        Assert.NotNull(metric);
        Assert.Equal("chat", metric!.GenerationSource);
        Assert.Equal(session.Id, metric.ChatSessionId);
    }

    // ── Golden Set 5: No places available → graceful 200 with fallback ────────

    [Fact]
    public async Task Builder_NoPlaces_Returns200_WithEmptyStopsAndWarning()
    {
        // Do NOT seed any places for this city — pero SÍ marcarla cubierta: desde m1/F4
        // /builder/chat rechaza ciudades no cubiertas antes del gate; aquí probamos el
        // soft-fallback de "ciudad cubierta pero sin catálogo todavía".
        fixture.MarkCityLive("Reykjavik");
        fixture.FakeGemini.Responder = _ => GeminiOk(JsonSerializer.Serialize(new
        {
            days = 1, categories = new[] { "food" }, vibes = new string[] { },
            groupType = "solo", planName = "Reykjavik Solo", maxStopsPerDay = 3
        }));

        var client = await fixture.CreateGenerationClientAsync();
        var res = await client.PostAsJsonAsync("/builder/chat", new
        {
            message = "trip to Reykjavik",
            tripContext = new { city = "Reykjavik", days = 1, groupType = "solo", categories = new[] { "food" } }
        });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(body.TryGetProperty("plan", out var plan), "plan missing");
        Assert.Equal("Reykjavik", plan.GetProperty("city").GetString());
        Assert.True(body.TryGetProperty("stops", out var stops));
        Assert.Equal(0, stops.GetArrayLength());
        Assert.True(body.TryGetProperty("warnings", out var warnings));
        var warningList = warnings.EnumerateArray().Select(w => w.GetString()).ToList();
        Assert.Contains("no_places_available", warningList);
    }

    // ── Golden Set 6: Day-1 ordering is always 0-based and contiguous ─────────

    [Fact]
    public async Task Builder_StopOrder_IsContiguous_StartingAtZero()
    {
        await SeedMiamiPlaces(6, "Food");

        fixture.FakeGemini.Responder = _ => GeminiOk(JsonSerializer.Serialize(new
        {
            days = 2,
            categories = new[] { "food" },
            vibes = new string[] { },
            groupType = "couple",
            planName = "Miami Couple 2 Days",
            maxStopsPerDay = 3
        }));

        var client = await fixture.CreateGenerationClientAsync();
        var res = await client.PostAsJsonAsync("/builder/chat", new
        {
            message = "2 días miami pareja",
            tripContext = new { city = Miami, days = 2, groupType = "couple", categories = new[] { "food" } }
        });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var stops = body.GetProperty("stops").EnumerateArray().ToList();

        // Group by day and verify each day's orderIndex starts at 0 and is contiguous.
        var byDay = stops
            .GroupBy(s => s.GetProperty("dayNumber").GetInt32())
            .OrderBy(g => g.Key);

        foreach (var dayGroup in byDay)
        {
            var orders = dayGroup.Select(s => s.GetProperty("orderIndex").GetInt32()).OrderBy(x => x).ToList();
            Assert.Equal(0, orders[0]);
            for (int i = 1; i < orders.Count; i++)
                Assert.Equal(orders[i - 1] + 1, orders[i]);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

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

    private async Task SeedMiamiPlaces(int count, string category)
    {
        var db = fixture.GetDbContext();
        var tag = Guid.NewGuid().ToString("N")[..8];
        for (int i = 0; i < count; i++)
        {
            db.Places.Add(new Place
            {
                Id = Guid.NewGuid(),
                Name = $"Golden {category} {tag}-{i}",
                Category = category,
                City = Miami,
                WhyThisPlace = "Golden set seed",
                Status = "published",
                BestTimes = new List<string> { "any" },
                Latitude = 25.77m + (decimal)(i * 0.01),
                Longitude = -80.19m + (decimal)(i * 0.01),
                GooglePlaceId = $"gpid-golden-{tag}-{i}",
                VisitDurationMin = 60
            });
        }
        await db.SaveChangesAsync();
    }

    private (HttpClient client, Guid userId) CreateAuthClient()
    {
        var email = $"golden-{Guid.NewGuid():N}@example.com";
        var db = fixture.GetDbContext();
        var user = new User { Id = Guid.NewGuid(), Email = email };
        db.Users.Add(user);
        db.SaveChanges();

        var token = fixture.CreateAppToken(user.Id, email);
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (client, user.Id);
    }
}
