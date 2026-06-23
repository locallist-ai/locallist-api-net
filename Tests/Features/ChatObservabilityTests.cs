using System.Net;
using System.Text;
using System.Text.Json;
using LocalList.API.NET.Features.Chat;
using LocalList.API.NET.Shared.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace LocalList.API.Tests.Features;

/// <summary>
/// Verifies that ChatTurn and PlanMetric rows are persisted with correct diagnostics
/// after AI calls. Guards against regression of the chat observability pipeline.
/// </summary>
public class ChatObservabilityTests(ApiFixture fixture) : IClassFixture<ApiFixture>, IDisposable
{
    private const string Miami = "Miami";

    public void Dispose()
    {
        fixture.FakeGemini.Responder = null;
        fixture.FakeGemini.Calls.Clear();
    }

    // ── /chat/turn — ChatTurn persistence ────────────────────────────────────

    [Fact]
    public async Task Turn_Gemini_PersistsChatTurnWithNoError()
    {
        await EnsureMiamiCity();

        fixture.FakeGemini.Responder = _ => SlotExtractorOk(new
        {
            extracted = new { },
            aiMessage = "What city are you visiting?",
            nextQuestion = "city",
            quickReplies = Array.Empty<object>()
        }, inputTokens: 120, outputTokens: 60, thinkingTokens: 15);

        var client = fixture.CreateClient();
        var res = await client.PostAsJsonAsync("/chat/turn", new { message = "I want food and culture" });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var db = fixture.GetDbContext();
        var turn = await db.ChatTurns
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync();

        Assert.NotNull(turn);
        Assert.Null(turn.ErrorCode);
        Assert.Equal("gemini", turn.AiProvider);
        Assert.Equal("slot-v1", turn.PromptVersion);
        Assert.Equal(200, turn.GeminiStatus); // HTTP 200
        Assert.False(string.IsNullOrEmpty(turn.PromptExcerpt));
    }

    [Fact]
    public async Task Turn_Gemini_PersistsTokenCountsAndCost()
    {
        await EnsureMiamiCity();

        fixture.FakeGemini.Responder = _ => SlotExtractorOk(new
        {
            extracted = new { },
            aiMessage = "Where are you heading?",
            nextQuestion = "city",
            quickReplies = Array.Empty<object>()
        }, inputTokens: 200, outputTokens: 80, thinkingTokens: 20);

        var client = fixture.CreateClient();
        var res = await client.PostAsJsonAsync("/chat/turn", new { message = "I enjoy outdoor activities" });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var db = fixture.GetDbContext();
        var turn = await db.ChatTurns
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync();

        Assert.NotNull(turn);
        Assert.Equal(200, turn.InputTokens);
        Assert.Equal(80, turn.OutputTokens);
        Assert.Equal(20, turn.ThinkingTokens);
        Assert.NotNull(turn.CostUsd);
        Assert.True(turn.CostUsd > 0m);
    }

    [Fact]
    public async Task Turn_GeminiFailure_PersistsChatTurnWithErrorCode()
    {
        await EnsureMiamiCity();

        fixture.FakeGemini.Responder = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("{\"error\":\"overloaded\"}", Encoding.UTF8, "application/json")
        };

        var client = fixture.CreateClient();
        var res = await client.PostAsJsonAsync("/chat/turn", new { message = "I want to visit restaurants" });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode); // endpoint succeeds with fallback

        var db = fixture.GetDbContext();
        var turn = await db.ChatTurns
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync();

        Assert.NotNull(turn);
        Assert.Equal("http_error", turn.ErrorCode);
        Assert.Equal(503, turn.GeminiStatus);
    }

    [Fact]
    public async Task Turn_GeminiFailure_CapturesRedactedErrorBodyInErrorMessage()
    {
        await EnsureMiamiCity();

        // 429 con cuerpo de cuota: el motivo real debe quedar en chat_turns.error_message
        // (truncado + redactado), no solo el status. Esto es lo que ve el admin.
        fixture.FakeGemini.Responder = _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent(
                "{\"error\":{\"code\":429,\"message\":\"Quota exceeded for quota metric 'generate_requests'\",\"status\":\"RESOURCE_EXHAUSTED\"}}",
                Encoding.UTF8, "application/json")
        };

        var client = fixture.CreateClient();
        var res = await client.PostAsJsonAsync("/chat/turn", new { message = "I want to visit restaurants" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var db = fixture.GetDbContext();
        var turn = await db.ChatTurns
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync();

        Assert.NotNull(turn);
        Assert.Equal("http_error", turn!.ErrorCode);
        Assert.Equal(429, turn.GeminiStatus);
        Assert.NotNull(turn.ErrorMessage);
        // El body real (cuota) queda consultable, no solo "HTTP 429".
        Assert.Contains("Quota exceeded", turn.ErrorMessage!);
        Assert.Contains("RESOURCE_EXHAUSTED", turn.ErrorMessage!);
    }

    [Fact]
    public async Task Turn_PiiInUserMessage_IsRedactedInChatTurn()
    {
        await EnsureMiamiCity();

        fixture.FakeGemini.Responder = _ => SlotExtractorOk(new
        {
            extracted = new { },
            aiMessage = "Got it!",
            nextQuestion = "city",
            quickReplies = Array.Empty<object>()
        });

        var client = fixture.CreateClient();
        var res = await client.PostAsJsonAsync("/chat/turn", new
        {
            message = "Contact me at secret.user@example.com for the trip"
        });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var db = fixture.GetDbContext();
        var turn = await db.ChatTurns
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync();

        Assert.NotNull(turn);
        Assert.DoesNotContain("secret.user@example.com", turn.UserMessage ?? "");
        Assert.Contains("[REDACTED:email]", turn.UserMessage ?? "");
    }

    // ── /chat/generate — ChatTurn + PlanMetric ───────────────────────────────

    [Fact]
    public async Task Generate_Authenticated_PersistsChatTurnAndPlanMetric()
    {
        await SeedMiamiPlaces(3);

        var db = fixture.GetDbContext();
        var userId = Guid.NewGuid();
        var email = $"obs-gen-{Guid.NewGuid():N}@test.com";
        db.Users.Add(new User { Id = userId, Email = email });
        var session = new ChatSession
        {
            UserId = userId,
            Status = "ready",
            TurnCount = 3,
            SlotsJson = JsonSerializer.Serialize(new ChatSlots
            {
                City = Miami, Days = 2, GroupType = "couple",
                Categories = new() { "food" }, Budget = "moderate"
            })
        };
        db.ChatSessions.Add(session);
        await db.SaveChangesAsync();

        fixture.FakeGemini.Responder = _ => PlanExtractionOk(new
        {
            days = 2, categories = new[] { "food" }, vibes = new string[] { },
            groupType = "couple", planName = "Obs Test Plan", maxStopsPerDay = 4
        });

        var client = fixture.CreateClient();
        var token = fixture.CreateAppToken(userId, email);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var res = await client.PostAsJsonAsync("/chat/generate", new { sessionId = session.Id });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var freshDb = fixture.GetDbContext();

        var metric = await freshDb.PlanMetrics
            .OrderByDescending(m => m.CreatedAt)
            .FirstOrDefaultAsync();

        Assert.NotNull(metric);
        Assert.Equal("chat", metric.GenerationSource);
        Assert.Equal(session.Id, metric.ChatSessionId);
        Assert.Equal(5, (int)metric.SignalsFilled); // all 5 slots filled
        Assert.Equal(2, metric.NumDays);

        var turn = await freshDb.ChatTurns
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync();

        Assert.NotNull(turn);
        Assert.Equal("slot-v1", turn.PromptVersion);
        Assert.Equal(session.Id, turn.SessionId);
        Assert.Equal(metric.GenerateTurnId, turn.Id);
    }

    [Fact]
    public async Task Generate_PlanMetric_SignalsFilledMatchesSlotCount()
    {
        await SeedMiamiPlaces(3);

        var db = fixture.GetDbContext();
        var userId = Guid.NewGuid();
        var email = $"obs-sig-{Guid.NewGuid():N}@test.com";
        db.Users.Add(new User { Id = userId, Email = email });
        // Only city + days filled (2/5 signals) — but we need ready to generate; set turn cap
        var session = new ChatSession
        {
            UserId = userId,
            Status = "active",
            TurnCount = 6, // at cap so generate is allowed
            SlotsJson = JsonSerializer.Serialize(new ChatSlots
            {
                City = Miami, Days = 1
                // GroupType, Categories, Budget left null
            })
        };
        db.ChatSessions.Add(session);
        await db.SaveChangesAsync();

        fixture.FakeGemini.Responder = _ => PlanExtractionOk(new
        {
            days = 1, categories = new[] { "food" }, vibes = new string[] { },
            groupType = "couple", planName = "Min Signals Plan", maxStopsPerDay = 3
        });

        var client = fixture.CreateClient();
        var token = fixture.CreateAppToken(userId, email);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var res = await client.PostAsJsonAsync("/chat/generate", new { sessionId = session.Id });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var freshDb = fixture.GetDbContext();
        var metric = await freshDb.PlanMetrics
            .OrderByDescending(m => m.CreatedAt)
            .FirstOrDefaultAsync();

        Assert.NotNull(metric);
        Assert.Equal(2, (int)metric.SignalsFilled); // only city + days
    }

    // ── PlanMetric — lifecycle updates ───────────────────────────────────────

    [Fact]
    public async Task GetPlan_SetsWasOpenedOnPlanMetric()
    {
        var db = fixture.GetDbContext();
        var userId = Guid.NewGuid();
        var email = $"obs-open-{Guid.NewGuid():N}@test.com";
        db.Users.Add(new User { Id = userId, Email = email });
        var plan = new Plan
        {
            Name = "Test Open Plan",
            City = Miami,
            Type = "ai",
            DurationDays = 1,
            IsPublic = true,
            CreatedById = userId,
        };
        db.Plans.Add(plan);
        var metric = new PlanMetric
        {
            PlanId = plan.Id,
            GenerationSource = "chat",
            NumDays = 1,
        };
        db.PlanMetrics.Add(metric);
        await db.SaveChangesAsync();

        var client = fixture.CreateClient();
        var res = await client.GetAsync($"/plans/{plan.Id}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var freshDb = fixture.GetDbContext();
        var updated = await freshDb.PlanMetrics.FirstAsync(m => m.PlanId == plan.Id);

        Assert.True(updated.WasOpened);
        Assert.NotNull(updated.OpenedAt);
    }

    [Fact]
    public async Task StartFollow_SetsWasFollowedOnPlanMetric()
    {
        var db = fixture.GetDbContext();
        var userId = Guid.NewGuid();
        var email = $"obs-follow-{Guid.NewGuid():N}@test.com";
        db.Users.Add(new User { Id = userId, Email = email });
        await db.SaveChangesAsync();

        var plan = new Plan
        {
            Name = "Follow Obs Plan",
            City = Miami,
            Type = "ai",
            DurationDays = 1,
            IsPublic = true,
            CreatedById = userId,
        };
        db.Plans.Add(plan);
        var metric = new PlanMetric
        {
            PlanId = plan.Id,
            GenerationSource = "chat",
            NumDays = 1,
        };
        db.PlanMetrics.Add(metric);
        var stop = new PlanStop
        {
            PlanId = plan.Id,
            PlaceId = await SeedOnePlace(),
            DayNumber = 1,
            OrderIndex = 0,
            TimeBlock = "morning"
        };
        db.PlanStops.Add(stop);
        await db.SaveChangesAsync();

        var client = fixture.CreateClient();
        var token = fixture.CreateAppToken(userId, email);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var res = await client.PostAsJsonAsync("/follow/start", new { planId = plan.Id });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);

        var freshDb = fixture.GetDbContext();
        var updated = await freshDb.PlanMetrics.FirstAsync(m => m.PlanId == plan.Id);

        Assert.True(updated.WasFollowed);
        Assert.NotNull(updated.FollowedAt);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task EnsureMiamiCity()
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
                Name = $"ObsTest Place {tag}-{i}",
                Category = "food",
                City = Miami,
                WhyThisPlace = "Seeded for observability tests",
                Status = "published",
                BestTimes = new List<string> { "any" },
                Latitude = 25.77m + (decimal)(i * 0.01),
                Longitude = -80.19m + (decimal)(i * 0.01),
                GooglePlaceId = $"gpid-obs-{tag}-{i}",
            });
        }
        await db.SaveChangesAsync();
    }

    private async Task<Guid> SeedOnePlace()
    {
        var db = fixture.GetDbContext();
        var tag = Guid.NewGuid().ToString("N")[..6];
        var place = new Place
        {
            Id = Guid.NewGuid(),
            Name = $"Follow Test Place {tag}",
            Category = "food",
            City = Miami,
            WhyThisPlace = "For follow obs test",
            Status = "published",
            BestTimes = new List<string> { "any" },
            Latitude = 25.77m,
            Longitude = -80.19m,
            GooglePlaceId = $"gpid-follow-{tag}",
        };
        db.Places.Add(place);
        await db.SaveChangesAsync();
        return place.Id;
    }

    private static HttpResponseMessage SlotExtractorOk(
        object slotJson,
        int inputTokens = 100,
        int outputTokens = 50,
        int thinkingTokens = 10)
    {
        var text = JsonSerializer.Serialize(slotJson);
        var envelope = new
        {
            candidates = new[]
            {
                new
                {
                    content = new { parts = new[] { new { text } } },
                    finishReason = "STOP"
                }
            },
            usageMetadata = new
            {
                promptTokenCount = inputTokens,
                candidatesTokenCount = outputTokens,
                thoughtsTokenCount = thinkingTokens
            }
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(envelope), Encoding.UTF8, "application/json")
        };
    }

    private static HttpResponseMessage PlanExtractionOk(object extracted)
    {
        var envelope = new
        {
            candidates = new[]
            {
                new
                {
                    content = new { parts = new[] { new { text = JsonSerializer.Serialize(extracted) } } },
                    finishReason = "STOP"
                }
            },
            usageMetadata = new
            {
                promptTokenCount = 300,
                candidatesTokenCount = 150,
                thoughtsTokenCount = 30
            }
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(envelope), Encoding.UTF8, "application/json")
        };
    }
}
