using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace LocalList.API.Tests.Features;

/// <summary>
/// Integración de la cadena de fallback LLM en /chat/turn: con Gemini caído y
/// OpenAI activado (host derivado con OpenAI:ApiKey), el turno se sirve desde el
/// segundo provider y chat_turns registra ai_provider/model reales.
/// </summary>
public class ChatLlmFallbackTests(ApiFixture fixture) : IClassFixture<ApiFixture>, IDisposable
{
    public void Dispose()
    {
        fixture.FakeGemini.Responder = null;
        fixture.FakeGemini.Calls.Clear();
        fixture.FakeOpenAi.Responder = null;
        fixture.FakeOpenAi.Calls.Clear();
    }

    private WebApplicationFactory<Program> WithOpenAiEnabled()
    {
        // La factory derivada no pasa por el CreateClient() de ApiFixture, así que
        // forzamos las migraciones aquí: sin esto, si esta clase corre primero en la
        // colección, chat_turns no existe aún y el handler devuelve 500.
        _ = fixture.GetDbContext();
        return fixture.WithWebHostBuilder(b => b.UseSetting("OpenAI:ApiKey", "test-openai-key"));
    }

    [Fact]
    public async Task Turn_GeminiDown_FallsBackToOpenAiAndPersistsRealProvider()
    {
        fixture.FakeGemini.Responder = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("{\"error\":\"overloaded\"}", Encoding.UTF8, "application/json")
        };
        fixture.FakeOpenAi.Responder = _ => OpenAiChatOk(new
        {
            extracted = new { city = "Miami" },
            aiMessage = "Got it, Miami! How many days?",
            nextQuestion = "days",
            quickReplies = Array.Empty<object>()
        });

        using var factory = WithOpenAiEnabled();
        var client = factory.CreateClient();
        var res = await client.PostAsJsonAsync("/chat/turn", new { message = "Quiero un plan en Miami" });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Got it, Miami! How many days?", body.GetProperty("aiMessage").GetString());

        Assert.Single(fixture.FakeGemini.Calls);
        Assert.Single(fixture.FakeOpenAi.Calls);

        var db = fixture.GetDbContext();
        var turn = await db.ChatTurns.OrderByDescending(t => t.CreatedAt).FirstOrDefaultAsync();
        Assert.NotNull(turn);
        Assert.Null(turn.ErrorCode);
        Assert.Equal("openai", turn.AiProvider);
        Assert.Equal("gpt-5-nano", turn.Model);
        Assert.Equal(200, turn.GeminiStatus); // status del provider que respondió
    }

    [Fact]
    public async Task Turn_BothProvidersDown_PersistsAttemptSummaryAndFallbackMessage()
    {
        fixture.FakeGemini.Responder = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("{\"error\":\"overloaded\"}", Encoding.UTF8, "application/json")
        };
        // FakeOpenAi sin Responder → 503 por defecto.

        using var factory = WithOpenAiEnabled();
        var client = factory.CreateClient();
        var res = await client.PostAsJsonAsync("/chat/turn", new { message = "Tres días en Madrid" });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode); // endpoint degrada con mensaje fallback

        var db = fixture.GetDbContext();
        var turn = await db.ChatTurns.OrderByDescending(t => t.CreatedAt).FirstOrDefaultAsync();
        Assert.NotNull(turn);
        Assert.Equal("http_error", turn.ErrorCode);
        Assert.Equal("openai", turn.AiProvider); // último intento de la cadena
        Assert.Contains("gemini: http_error(503)", turn.ErrorMessage);
        Assert.Contains("openai: http_error(503)", turn.ErrorMessage);
    }

    [Fact]
    public async Task Turn_FallbackProviderReturnsMalformed200_DegradesInsteadOf500()
    {
        // Gemini caído + OpenAI devuelve 200 con cuerpo NO-JSON (HTML de un gateway). Antes la
        // JsonException escapaba de la cadena → 500. Ahora cuenta como fallo del provider y el
        // turno degrada con mensaje fallback (200), persistiendo el error real.
        fixture.FakeGemini.Responder = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("{\"error\":\"overloaded\"}", Encoding.UTF8, "application/json")
        };
        fixture.FakeOpenAi.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><body>504 Gateway Timeout</body></html>", Encoding.UTF8, "text/html")
        };

        using var factory = WithOpenAiEnabled();
        var client = factory.CreateClient();
        var res = await client.PostAsJsonAsync("/chat/turn", new { message = "Dos días en Sevilla" });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var db = fixture.GetDbContext();
        var turn = await db.ChatTurns.OrderByDescending(t => t.CreatedAt).FirstOrDefaultAsync();
        Assert.NotNull(turn);
        Assert.Equal("parse_error", turn.ErrorCode);
        Assert.Equal("openai", turn.AiProvider);
        Assert.Contains("gemini: http_error(503)", turn.ErrorMessage);
        Assert.Contains("openai: parse_error", turn.ErrorMessage);
    }

    [Fact]
    public async Task Turn_GeminiHealthy_DoesNotTouchOpenAi()
    {
        fixture.FakeGemini.Responder = _ => GeminiSlotOk(new
        {
            extracted = new { },
            aiMessage = "Where to?",
            nextQuestion = "city",
            quickReplies = Array.Empty<object>()
        });

        using var factory = WithOpenAiEnabled();
        var client = factory.CreateClient();
        var res = await client.PostAsJsonAsync("/chat/turn", new { message = "hola" });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Empty(fixture.FakeOpenAi.Calls);

        var db = fixture.GetDbContext();
        var turn = await db.ChatTurns.OrderByDescending(t => t.CreatedAt).FirstOrDefaultAsync();
        Assert.NotNull(turn);
        Assert.Equal("gemini", turn.AiProvider);
        Assert.Equal("gemini-2.5-flash", turn.Model);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static HttpResponseMessage OpenAiChatOk(object slotJson)
    {
        var envelope = new
        {
            choices = new[]
            {
                new
                {
                    message = new { content = JsonSerializer.Serialize(slotJson) },
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

    private static HttpResponseMessage GeminiSlotOk(object slotJson)
    {
        var envelope = new
        {
            candidates = new[]
            {
                new
                {
                    content = new { parts = new[] { new { text = JsonSerializer.Serialize(slotJson) } } },
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
