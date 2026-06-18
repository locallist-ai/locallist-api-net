using System.Net;
using System.Text.Json;
using LocalList.API.NET.Shared.AI.Llm;
using LocalList.API.NET.Shared.AI.Llm.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalList.API.Tests.Unit.Llm;

public class GeminiLlmClientTests
{
    private static readonly LlmJsonRequest Request = new("extract slots", 0.2, 200);

    private const string OkBody = """
        {
          "candidates": [{"content": {"parts": [{"text": "{\"ok\":true}"}]}, "finishReason": "STOP"}],
          "usageMetadata": {"promptTokenCount": 100, "candidatesTokenCount": 50, "thoughtsTokenCount": 10}
        }
        """;

    private static GeminiLlmClient Client(HttpMessageHandler handler, int minOutputTokens = 0) =>
        new(new HttpClient(handler), "test-key", "gemini-2.5-flash", NullLogger.Instance, minOutputTokens);

    [Fact]
    public async Task RequestShape_UsesGoogApiKeyHeaderAndGenerationConfig()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, OkBody);
        await Client(handler).GenerateJsonAsync(Request);

        Assert.Equal(
            "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent",
            handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("test-key", handler.LastRequest.Headers.GetValues("x-goog-api-key").Single());

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var root = body.RootElement;
        Assert.Equal("extract slots",
            root.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString());
        var generationConfig = root.GetProperty("generationConfig");
        Assert.Equal(0.2, generationConfig.GetProperty("temperature").GetDouble());
        Assert.Equal(200, generationConfig.GetProperty("maxOutputTokens").GetInt32());
        Assert.Equal("application/json", generationConfig.GetProperty("responseMimeType").GetString());
        // Sin JsonSchema en la request no se envía responseSchema (JSON pedido por mimeType).
        Assert.False(generationConfig.TryGetProperty("responseSchema", out _));
    }

    [Fact]
    public async Task RequestShape_DisablesThinking_WithThinkingBudgetZero()
    {
        // gemini-2.5-flash trae thinking ON por defecto; thinkingBudget=0 lo desactiva
        // para que los thinking-tokens no truncen el JSON contra maxOutputTokens.
        var handler = new CapturingHandler(HttpStatusCode.OK, OkBody);
        await Client(handler).GenerateJsonAsync(Request);

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var thinking = body.RootElement.GetProperty("generationConfig").GetProperty("thinkingConfig");
        Assert.Equal(0, thinking.GetProperty("thinkingBudget").GetInt32());
    }

    [Fact]
    public async Task MinOutputTokens_FloorsMaxOutputTokens()
    {
        // Request pide 200, pero el suelo (1024) gana — espejo del cliente OpenAI.
        var handler = new CapturingHandler(HttpStatusCode.OK, OkBody);
        await Client(handler, minOutputTokens: 1024).GenerateJsonAsync(Request);

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var maxOutputTokens = body.RootElement.GetProperty("generationConfig")
            .GetProperty("maxOutputTokens").GetInt32();
        Assert.Equal(1024, maxOutputTokens);
    }

    [Fact]
    public async Task MinOutputTokens_DoesNotLowerLargerRequest()
    {
        // Si la request pide más que el suelo, se respeta la request.
        var handler = new CapturingHandler(HttpStatusCode.OK, OkBody);
        await Client(handler, minOutputTokens: 1024).GenerateJsonAsync(Request with { MaxOutputTokens = 2048 });

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var maxOutputTokens = body.RootElement.GetProperty("generationConfig")
            .GetProperty("maxOutputTokens").GetInt32();
        Assert.Equal(2048, maxOutputTokens);
    }

    [Fact]
    public async Task MaxTokensFinishReason_MapsToTruncatedNotInvalidJson()
    {
        // JSON cortado por MAX_TOKENS: fallo explícito "truncated", no se entrega como éxito
        // ni se deja a TryCleanJson reportarlo como invalid_json genérico.
        const string truncated = """
            {
              "candidates": [{"content": {"parts": [{"text": "{\"city\":\"Mia"}]}, "finishReason": "MAX_TOKENS"}],
              "usageMetadata": {"promptTokenCount": 100, "candidatesTokenCount": 512, "thoughtsTokenCount": 480}
            }
            """;
        var handler = new CapturingHandler(HttpStatusCode.OK, truncated);
        var response = await Client(handler).GenerateJsonAsync(Request);

        Assert.False(response.Succeeded);
        Assert.Equal("truncated", response.Diagnostics.ErrorCode);
        Assert.Equal("MAX_TOKENS", response.Diagnostics.FinishReason);
        Assert.Equal(512, response.Diagnostics.OutputTokens);
        Assert.Equal(480, response.Diagnostics.ThinkingTokens);
    }

    [Fact]
    public async Task JsonSchema_AddsResponseSchemaToGenerationConfig()
    {
        using var schemaDoc = JsonDocument.Parse(
            """{"type":"object","properties":{"ok":{"type":"boolean"}}}""");
        var request = Request with { JsonSchema = schemaDoc.RootElement.Clone() };

        var handler = new CapturingHandler(HttpStatusCode.OK, OkBody);
        await Client(handler).GenerateJsonAsync(request);

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var schema = body.RootElement.GetProperty("generationConfig").GetProperty("responseSchema");
        Assert.Equal("object", schema.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Ok_ParsesTextTokensAndCost()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, OkBody);
        var response = await Client(handler).GenerateJsonAsync(Request);

        Assert.True(response.Succeeded);
        Assert.Equal("{\"ok\":true}", response.Text);
        Assert.Equal("gemini", response.Diagnostics.Provider);
        Assert.Equal("gemini-2.5-flash", response.Diagnostics.Model);
        Assert.Equal(100, response.Diagnostics.InputTokens);
        Assert.Equal(50, response.Diagnostics.OutputTokens);
        Assert.Equal(10, response.Diagnostics.ThinkingTokens);
        Assert.Equal(160, response.Diagnostics.TotalTokens);
        Assert.Equal("STOP", response.Diagnostics.FinishReason);
        Assert.NotNull(response.Diagnostics.CostUsd);
    }

    [Fact]
    public async Task SafetyEmptyParts_MapsToContentFiltered()
    {
        const string body = """
            {
              "candidates": [{"content": {"parts": []}, "finishReason": "SAFETY"}],
              "usageMetadata": {"promptTokenCount": 100, "candidatesTokenCount": 0}
            }
            """;
        var handler = new CapturingHandler(HttpStatusCode.OK, body);
        var response = await Client(handler).GenerateJsonAsync(Request);

        Assert.False(response.Succeeded);
        Assert.Equal("content_filtered", response.Diagnostics.ErrorCode);
        Assert.Equal("SAFETY", response.Diagnostics.FinishReason);
    }

    [Fact]
    public async Task Timeout_MapsToTimeoutErrorCode()
    {
        // El timeout interno de HttpClient lanza TaskCanceledException sin que el ct del
        // caller esté cancelado — el provider lo convierte en fallo "timeout" (no propaga).
        var handler = new ThrowingHandler(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"));
        var response = await Client(handler).GenerateJsonAsync(Request);

        Assert.False(response.Succeeded);
        Assert.Equal("timeout", response.Diagnostics.ErrorCode);
        Assert.Null(response.Diagnostics.HttpStatus);
    }

    [Fact]
    public async Task HttpError_ReturnsFailureWithStatus()
    {
        var handler = new CapturingHandler(HttpStatusCode.ServiceUnavailable, "{\"error\":\"overloaded\"}");
        var response = await Client(handler).GenerateJsonAsync(Request);

        Assert.False(response.Succeeded);
        Assert.Equal("http_error", response.Diagnostics.ErrorCode);
        Assert.Equal(503, response.Diagnostics.HttpStatus);
    }

    [Fact]
    public async Task Malformed200_MapsToParseErrorNotThrow()
    {
        // 200 con cuerpo no-JSON (página HTML de un proxy/gateway): debe degradar a fallo, no lanzar.
        var handler = new CapturingHandler(HttpStatusCode.OK, "<html><body>502 Bad Gateway</body></html>");
        var response = await Client(handler).GenerateJsonAsync(Request);

        Assert.False(response.Succeeded);
        Assert.Equal("parse_error", response.Diagnostics.ErrorCode);
        Assert.NotNull(response.Diagnostics.ResponseRaw);
    }

    [Fact]
    public async Task UnexpectedException_MapsToProviderErrorNotThrow()
    {
        // Polly TimeoutRejectedException (AttemptTimeout) no es OCE ni HttpRequestException:
        // antes escapaba del catch y abortaba la cadena. Ahora se mapea a provider_error.
        var handler = new ThrowingHandler(new InvalidOperationException("resilience pipeline timed out"));
        var response = await Client(handler).GenerateJsonAsync(Request);

        Assert.False(response.Succeeded);
        Assert.Equal("provider_error", response.Diagnostics.ErrorCode);
    }

    [Fact]
    public async Task Ok_CostIncludesThinkingTokens()
    {
        // 100 in · 50 out · 10 thinking → (100·0.30 + (50+10)·2.50) / 1e6.
        var handler = new CapturingHandler(HttpStatusCode.OK, OkBody);
        var response = await Client(handler).GenerateJsonAsync(Request);

        var expected = (100 * 0.30m + (50 + 10) * 2.50m) / 1_000_000m;
        Assert.Equal(expected, response.Diagnostics.CostUsd);
    }

    [Fact]
    public async Task CallerCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var handler = new ThrowingHandler(new OperationCanceledException(cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Client(handler).GenerateJsonAsync(Request, cts.Token));
    }
}
