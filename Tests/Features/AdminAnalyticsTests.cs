using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.Tests.Features;

public class AdminAnalyticsTests(ApiFixture fixture) : IClassFixture<ApiFixture>
{
    // ── ChatTurns — list ─────────────────────────────────────────────────────

    [Fact]
    public async Task ChatTurns_List_ResponseShape_IsCorrect()
    {
        // Verify endpoint returns the correct response shape (no direct ChatTurn seeding
        // since session_id FK requires a real chat_sessions row; use future date filter
        // to get a predictable empty result and check structure).
        var client = CreateAdminClient();
        var future = DateTimeOffset.UtcNow.AddYears(10).ToString("o");
        var res = await client.GetAsync($"/admin/analytics/chat-turns?limit=10&offset=0&from={Uri.EscapeDataString(future)}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("turns", out var turns));
        Assert.True(body.TryGetProperty("total", out var total));
        Assert.True(body.TryGetProperty("limit", out var limit));
        Assert.True(body.TryGetProperty("offset", out var offset));
        Assert.Equal(0, total.GetInt32());
        Assert.Equal(10, limit.GetInt32());
        Assert.Equal(0, offset.GetInt32());
        Assert.Equal(JsonValueKind.Array, turns.ValueKind);
    }

    [Fact]
    public async Task ChatTurns_List_FiltersBy_HasError()
    {
        var db = fixture.GetDbContext();
        db.ChatTurns.AddRange(
            new ChatTurn
            {
                Id = Guid.NewGuid(), TurnIndex = 0, AiProvider = "gemini",
                Model = "gemini-2.5-flash", PromptVersion = "slot-v1",
                PromptChars = 50, LatencyMs = 200, ErrorCode = "http_error"
            },
            new ChatTurn
            {
                Id = Guid.NewGuid(), TurnIndex = 0, AiProvider = "gemini",
                Model = "gemini-2.5-flash", PromptVersion = "slot-v1",
                PromptChars = 50, LatencyMs = 200
            });
        await db.SaveChangesAsync();

        var client = CreateAdminClient();
        var res = await client.GetAsync("/admin/analytics/chat-turns?hasError=true");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var turns = body.GetProperty("turns").EnumerateArray().ToList();
        Assert.True(turns.All(t =>
            t.TryGetProperty("errorCode", out var ec) && ec.GetString() != null));
    }

    [Fact]
    public async Task ChatTurns_List_RequiresAdmin()
    {
        var client = fixture.CreateClient();
        var res = await client.GetAsync("/admin/analytics/chat-turns");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    // ── ChatTurns — stats ────────────────────────────────────────────────────

    [Fact]
    public async Task ChatTurns_Stats_ReturnsCorrectAggregates()
    {
        var db = fixture.GetDbContext();
        db.ChatTurns.AddRange(
            new ChatTurn
            {
                Id = Guid.NewGuid(), TurnIndex = 0, AiProvider = "gemini",
                Model = "gemini-2.5-flash", PromptVersion = "slot-v1",
                PromptChars = 100, LatencyMs = 400, InputTokens = 100, OutputTokens = 50,
                CostUsd = 0.001m, FinishReason = "STOP", SlotCompleteness = 60
            },
            new ChatTurn
            {
                Id = Guid.NewGuid(), TurnIndex = 1, AiProvider = "gemini",
                Model = "gemini-2.5-flash", PromptVersion = "slot-v1",
                PromptChars = 200, LatencyMs = 600, InputTokens = 200, OutputTokens = 80,
                CostUsd = 0.002m, FinishReason = "STOP", ErrorCode = "timeout", SlotCompleteness = 80
            });
        await db.SaveChangesAsync();

        var client = CreateAdminClient();
        var res = await client.GetAsync("/admin/analytics/chat-turns/stats");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("totalTurns", out var total));
        Assert.True(total.GetInt32() >= 2);
        Assert.True(body.TryGetProperty("avgLatencyMs", out _));
        Assert.True(body.TryGetProperty("totalInputTokens", out _));
        Assert.True(body.TryGetProperty("totalCostUsd", out _));
        Assert.True(body.TryGetProperty("errorRate", out var errorRate));
        Assert.True(errorRate.GetDouble() >= 0 && errorRate.GetDouble() <= 1);
        Assert.True(body.TryGetProperty("finishReasonBreakdown", out var frd));
        Assert.True(frd.TryGetProperty("STOP", out _));
        Assert.True(body.TryGetProperty("errorCodeBreakdown", out var ebd));
        Assert.True(ebd.TryGetProperty("timeout", out _));
    }

    [Fact]
    public async Task ChatTurns_Stats_Empty_ReturnsZeroes()
    {
        var client = CreateAdminClient();
        // Request with a future date range that will have no data
        var from = DateTimeOffset.UtcNow.AddYears(10).ToString("o");
        var res = await client.GetAsync($"/admin/analytics/chat-turns/stats?from={Uri.EscapeDataString(from)}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("totalTurns").GetInt32());
        Assert.Equal(0, body.GetProperty("avgLatencyMs").GetDouble());
        Assert.Equal(0, body.GetProperty("errorRate").GetDouble());
    }

    // ── PlanMetrics — list ───────────────────────────────────────────────────

    [Fact]
    public async Task PlanMetrics_List_ReturnsPagedResults()
    {
        var db = fixture.GetDbContext();
        var plan = new Plan { Id = Guid.NewGuid(), Name = "Analytics Plan", City = "Lisbon", Type = "ai" };
        db.Plans.Add(plan);
        db.PlanMetrics.Add(new PlanMetric
        {
            Id = Guid.NewGuid(), PlanId = plan.Id, GenerationSource = "chat",
            SignalsFilled = 4, NumDays = 3, NumStops = 9, NumCategories = 2,
            GroupType = "couple", Budget = "moderate", LatencyMs = 800, CostUsd = 0.005m
        });
        await db.SaveChangesAsync();

        var client = CreateAdminClient();
        var res = await client.GetAsync("/admin/analytics/plan-metrics?limit=10");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("metrics", out var metrics));
        Assert.True(body.TryGetProperty("total", out var total));
        Assert.True(total.GetInt32() >= 1);

        var first = metrics.EnumerateArray().First();
        Assert.True(first.TryGetProperty("id", out _));
        Assert.True(first.TryGetProperty("planId", out _));
        Assert.True(first.TryGetProperty("planCity", out _));
        Assert.True(first.TryGetProperty("numDays", out _));
        Assert.True(first.TryGetProperty("wasOpened", out _));
        Assert.True(first.TryGetProperty("wasFollowed", out _));
    }

    [Fact]
    public async Task PlanMetrics_List_FiltersBy_City()
    {
        var db = fixture.GetDbContext();
        var planMad = new Plan { Id = Guid.NewGuid(), Name = "Madrid Plan", City = "Madrid", Type = "ai" };
        var planLis = new Plan { Id = Guid.NewGuid(), Name = "Lisbon Plan 2", City = "Lisbon", Type = "ai" };
        db.Plans.AddRange(planMad, planLis);
        db.PlanMetrics.AddRange(
            new PlanMetric
            {
                Id = Guid.NewGuid(), PlanId = planMad.Id, GenerationSource = "chat",
                SignalsFilled = 3, NumDays = 1, NumStops = 3, NumCategories = 1, LatencyMs = 500
            },
            new PlanMetric
            {
                Id = Guid.NewGuid(), PlanId = planLis.Id, GenerationSource = "builder",
                SignalsFilled = 5, NumDays = 2, NumStops = 6, NumCategories = 2, LatencyMs = 600
            });
        await db.SaveChangesAsync();

        var client = CreateAdminClient();
        var res = await client.GetAsync("/admin/analytics/plan-metrics?city=Madrid");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var metrics = body.GetProperty("metrics").EnumerateArray().ToList();
        Assert.True(metrics.All(m =>
            m.TryGetProperty("planCity", out var pc) && pc.GetString() == "Madrid"));
    }

    // ── PlanMetrics — stats ──────────────────────────────────────────────────

    [Fact]
    public async Task PlanMetrics_Stats_ReturnsCorrectRates()
    {
        var db = fixture.GetDbContext();
        var city = $"StatsCity-{Guid.NewGuid():N}"[..28];
        var p1 = new Plan { Id = Guid.NewGuid(), Name = "Stats Plan 1", City = city, Type = "ai" };
        var p2 = new Plan { Id = Guid.NewGuid(), Name = "Stats Plan 2", City = city, Type = "ai" };
        var p3 = new Plan { Id = Guid.NewGuid(), Name = "Stats Plan 3", City = city, Type = "ai" };
        db.Plans.AddRange(p1, p2, p3);
        db.PlanMetrics.AddRange(
            new PlanMetric
            {
                Id = Guid.NewGuid(), PlanId = p1.Id, GenerationSource = "chat",
                SignalsFilled = 5, NumDays = 3, NumStops = 9, NumCategories = 2,
                LatencyMs = 700, WasOpened = true, WasFollowed = true
            },
            new PlanMetric
            {
                Id = Guid.NewGuid(), PlanId = p2.Id, GenerationSource = "chat",
                SignalsFilled = 4, NumDays = 2, NumStops = 6, NumCategories = 1,
                LatencyMs = 500, WasOpened = true, WasFollowed = false
            },
            new PlanMetric
            {
                Id = Guid.NewGuid(), PlanId = p3.Id, GenerationSource = "builder",
                SignalsFilled = 3, NumDays = 1, NumStops = 3, NumCategories = 1,
                LatencyMs = 400, WasOpened = false, WasFollowed = false
            });
        await db.SaveChangesAsync();

        var client = CreateAdminClient();
        var res = await client.GetAsync($"/admin/analytics/plan-metrics/stats?city={Uri.EscapeDataString(city)}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, body.GetProperty("totalPlans").GetInt32());

        var openRate = body.GetProperty("openRate").GetDouble();
        var followRate = body.GetProperty("followRate").GetDouble();
        Assert.Equal(2.0 / 3.0, openRate, precision: 5);
        Assert.Equal(1.0 / 3.0, followRate, precision: 5);

        var byCity = body.GetProperty("byCity").EnumerateArray().ToList();
        Assert.Single(byCity);
        Assert.Equal(city, byCity[0].GetProperty("city").GetString());
        Assert.Equal(3, byCity[0].GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task PlanMetrics_Stats_Empty_ReturnsZeroes()
    {
        var client = CreateAdminClient();
        var from = DateTimeOffset.UtcNow.AddYears(10).ToString("o");
        var res = await client.GetAsync($"/admin/analytics/plan-metrics/stats?from={Uri.EscapeDataString(from)}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("totalPlans").GetInt32());
        Assert.Equal(0, body.GetProperty("openRate").GetDouble());
        Assert.Equal(0, body.GetProperty("followRate").GetDouble());
        Assert.Empty(body.GetProperty("byCity").EnumerateArray().ToList());
    }

    [Fact]
    public async Task PlanMetrics_List_RequiresAdmin()
    {
        var client = fixture.CreateClient();
        var res = await client.GetAsync("/admin/analytics/plan-metrics");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private HttpClient CreateAdminClient()
    {
        var adminEmail = $"admin-analytics-{Guid.NewGuid():N}@locallist.ai";
        var adminFbUid = $"fb-admin-analytics-{Guid.NewGuid():N}";

        var db = fixture.GetDbContext();
        db.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Email = adminEmail,
            FirebaseUid = adminFbUid,
            Role = "admin"
        });
        db.SaveChanges();

        var client = fixture.CreateClient();
        var token = fixture.CreateToken(adminFbUid, adminEmail);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
