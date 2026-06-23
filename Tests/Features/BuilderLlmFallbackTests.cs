using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LocalList.API.NET.Shared.Data.Entities;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace LocalList.API.Tests.Features;

/// <summary>
/// Integración de la cadena de fallback LLM en /builder/chat (espejo de
/// <see cref="ChatLlmFallbackTests"/>): con Gemini caído y OpenAI activado
/// (host derivado con OpenAI:ApiKey), la extracción de preferencias se sirve
/// desde el segundo provider, el plan se genera igualmente y plan_metrics
/// registra el ai_provider real.
/// </summary>
public class BuilderLlmFallbackTests(ApiFixture fixture) : IClassFixture<ApiFixture>, IDisposable
{
    private const string Miami = "Miami";

    public void Dispose()
    {
        fixture.FakeGemini.Responder = null;
        fixture.FakeGemini.Calls.Clear();
        fixture.FakeOpenAi.Responder = null;
        fixture.FakeOpenAi.Calls.Clear();
        fixture.FakeEmbeddings.Responder = null;
        fixture.FakeEmbeddings.Calls.Clear();
    }

    private WebApplicationFactory<Program> WithOpenAiEnabled()
    {
        // La factory derivada no pasa por el CreateClient() de ApiFixture, así que
        // forzamos las migraciones aquí (incluye plan_metrics.ai_provider).
        _ = fixture.GetDbContext();
        return fixture.WithWebHostBuilder(b => b.UseSetting("OpenAI:ApiKey", "test-openai-key"));
    }

    private async Task<HttpClient> CreateAuthenticatedClient(WebApplicationFactory<Program> factory)
    {
        var userId = Guid.NewGuid();
        var fbUid = $"fb-builder-fallback-{Guid.NewGuid():N}";
        var email = $"builder-fallback-{Guid.NewGuid():N}@test.com";
        var db = fixture.GetDbContext();
        db.Users.Add(new User { Id = userId, Email = email, FirebaseUid = fbUid, Role = "user" });
        await db.SaveChangesAsync();

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", fixture.CreateToken(fbUid, email));
        return client;
    }

    [Fact]
    public async Task Chat_GeminiDown_FallsBackToOpenAiAndPersistsRealProvider()
    {
        await SeedPublishedMiamiPlaces(3);

        fixture.FakeGemini.Responder = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("{\"error\":\"overloaded\"}", Encoding.UTF8, "application/json")
        };
        fixture.FakeOpenAi.Responder = _ => OpenAiChatOk(new
        {
            days = 1,
            categories = new[] { "food" },
            vibes = new[] { "romantic" },
            groupType = "couple",
            planName = "Fallback Plan",
            maxStopsPerDay = 4
        });

        using var factory = WithOpenAiEnabled();
        var client = await CreateAuthenticatedClient(factory);

        var res = await client.PostAsJsonAsync("/builder/chat", new
        {
            message = "Plan romántico de comida en Miami",
            tripContext = new { city = Miami, days = 1, groupType = "couple" }
        });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.TryGetProperty("plan", out var plan), "Respuesta sin propiedad plan");
        Assert.True(body.GetProperty("stops").GetArrayLength() >= 1, "Se esperaba al menos una stop");
        var planId = plan.GetProperty("id").GetGuid();

        Assert.Single(fixture.FakeGemini.Calls);
        Assert.Single(fixture.FakeOpenAi.Calls);

        var db = fixture.GetDbContext();
        var metric = await db.PlanMetrics.FirstOrDefaultAsync(m => m.PlanId == planId);
        Assert.NotNull(metric);
        Assert.Equal("openai", metric.AiProvider);
        Assert.Equal("builder", metric.GenerationSource);
    }

    [Fact]
    public async Task Chat_GeminiHealthy_PersistsGeminiWithoutTouchingOpenAi()
    {
        await SeedPublishedMiamiPlaces(3);

        var extracted = new
        {
            days = 1,
            categories = new[] { "food" },
            vibes = Array.Empty<string>(),
            groupType = "couple",
            planName = "Primary Plan",
            maxStopsPerDay = 4
        };
        fixture.FakeGemini.Responder = _ => GeminiOk(JsonSerializer.Serialize(extracted));

        using var factory = WithOpenAiEnabled();
        var client = await CreateAuthenticatedClient(factory);

        var res = await client.PostAsJsonAsync("/builder/chat", new
        {
            message = "Plan de comida en Miami",
            tripContext = new { city = Miami, days = 1, groupType = "couple" }
        });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var planId = body.GetProperty("plan").GetProperty("id").GetGuid();

        Assert.Empty(fixture.FakeOpenAi.Calls);

        var db = fixture.GetDbContext();
        var metric = await db.PlanMetrics.FirstOrDefaultAsync(m => m.PlanId == planId);
        Assert.NotNull(metric);
        Assert.Equal("gemini", metric.AiProvider);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

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
                Name = $"Builder Fallback Seed {tag}-{i}",
                Category = "Food",
                City = Miami,
                WhyThisPlace = "Seeded para test de fallback de /builder/chat",
                Status = "published",
                BestTimes = new List<string> { "any" },
                Latitude = 25.77m + (decimal)(i * 0.01),
                Longitude = -80.19m + (decimal)(i * 0.01),
                GooglePlaceId = $"gpid-builder-fallback-{tag}-{i}",
            });
        }
        await db.SaveChangesAsync();
        return ids;
    }

    private static HttpResponseMessage OpenAiChatOk(object extractedJson)
    {
        var envelope = new
        {
            choices = new[]
            {
                new
                {
                    message = new { content = JsonSerializer.Serialize(extractedJson) },
                    finish_reason = "stop"
                }
            },
            usage = new
            {
                prompt_tokens = 110,
                completion_tokens = 45,
                completion_tokens_details = new { reasoning_tokens = 20 }
            }
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(envelope), Encoding.UTF8, "application/json")
        };
    }

    private static HttpResponseMessage GeminiOk(string text)
    {
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
            usageMetadata = new { promptTokenCount = 100, candidatesTokenCount = 50, thoughtsTokenCount = 10 }
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(envelope), Encoding.UTF8, "application/json")
        };
    }
}
