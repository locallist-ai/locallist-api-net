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

    private static GeminiLlmClient Client(HttpMessageHandler handler) =>
        new(new HttpClient(handler), "test-key", "gemini-2.5-flash", NullLogger.Instance);

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

    /// <summary>Handler que simula fallos de transporte (timeout interno de HttpClient, red caída…).</summary>
    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(exception);
    }
}
