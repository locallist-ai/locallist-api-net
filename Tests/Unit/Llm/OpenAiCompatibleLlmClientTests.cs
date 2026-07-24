using System.Net;
using System.Text.Json;
using LocalList.API.NET.Shared.AI.Llm;
using LocalList.API.NET.Shared.AI.Llm.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalList.API.Tests.Unit.Llm;

public class OpenAiCompatibleLlmClientTests
{
    private static readonly LlmJsonRequest Request = new("extract slots", 0.2, 200);

    private const string OkBody = """
        {
          "choices": [{"message": {"content": "{\"ok\":true}"}, "finish_reason": "stop"}],
          "usage": {"prompt_tokens": 120, "completion_tokens": 40,
                    "completion_tokens_details": {"reasoning_tokens": 32}}
        }
        """;

    private static OpenAiCompatibleLlmClient OpenAiClient(HttpMessageHandler handler) =>
        new(new HttpClient(handler), "test-key", "openai", "https://api.openai.com/v1", "gpt-5.4-nano",
            NullLogger.Instance,
            usesMaxCompletionTokens: true, supportsTemperature: false,
            minOutputTokens: 1024, reasoningEffort: "minimal");

    private static OpenAiCompatibleLlmClient MistralClient(CapturingHandler handler) =>
        new(new HttpClient(handler), "test-key", "mistral", "https://api.mistral.ai/v1", "mistral-small-latest",
            NullLogger.Instance);

    [Fact]
    public async Task OpenAi_RequestShape_UsesMaxCompletionTokensAndOmitsTemperature()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, OkBody);
        await OpenAiClient(handler).GenerateJsonAsync(Request);

        Assert.Equal("https://api.openai.com/v1/chat/completions", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("Bearer test-key", handler.LastRequest.Headers.GetValues("Authorization").Single());

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var root = body.RootElement;
        Assert.Equal("gpt-5.4-nano", root.GetProperty("model").GetString());
        Assert.Equal("json_object", root.GetProperty("response_format").GetProperty("type").GetString());
        // GPT-5 Nano: max_completion_tokens con mínimo 1024 (reasoning tokens), sin temperature.
        Assert.Equal(1024, root.GetProperty("max_completion_tokens").GetInt32());
        Assert.False(root.TryGetProperty("max_tokens", out _));
        Assert.False(root.TryGetProperty("temperature", out _));
        Assert.Equal("minimal", root.GetProperty("reasoning_effort").GetString());
    }

    [Fact]
    public async Task Mistral_RequestShape_UsesClassicMaxTokensAndTemperature()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, OkBody);
        await MistralClient(handler).GenerateJsonAsync(Request);

        Assert.Equal("https://api.mistral.ai/v1/chat/completions", handler.LastRequest!.RequestUri!.ToString());

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var root = body.RootElement;
        Assert.Equal(200, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal(0.2, root.GetProperty("temperature").GetDouble());
        Assert.False(root.TryGetProperty("max_completion_tokens", out _));
        Assert.False(root.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public async Task Ok_ParsesTextTokensAndCost()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, OkBody);
        var response = await OpenAiClient(handler).GenerateJsonAsync(Request);

        Assert.True(response.Succeeded);
        Assert.Equal("{\"ok\":true}", response.Text);
        Assert.Equal("openai", response.Diagnostics.Provider);
        Assert.Equal("gpt-5.4-nano", response.Diagnostics.Model);
        Assert.Equal(120, response.Diagnostics.InputTokens);
        Assert.Equal(40, response.Diagnostics.OutputTokens);
        Assert.Equal(32, response.Diagnostics.ThinkingTokens);
        Assert.Equal("stop", response.Diagnostics.FinishReason);
        Assert.NotNull(response.Diagnostics.CostUsd);
    }

    [Fact]
    public async Task HttpError_ReturnsFailureWithStatus()
    {
        var handler = new CapturingHandler(HttpStatusCode.TooManyRequests, "{\"error\":\"rate limit\"}");
        var response = await OpenAiClient(handler).GenerateJsonAsync(Request);

        Assert.False(response.Succeeded);
        Assert.Equal("http_error", response.Diagnostics.ErrorCode);
        Assert.Equal(429, response.Diagnostics.HttpStatus);
    }

    [Fact]
    public async Task ContentFilter_MapsToContentFiltered()
    {
        var body = """
            {"choices": [{"message": {"content": ""}, "finish_reason": "content_filter"}],
             "usage": {"prompt_tokens": 100, "completion_tokens": 0}}
            """;
        var handler = new CapturingHandler(HttpStatusCode.OK, body);
        var response = await OpenAiClient(handler).GenerateJsonAsync(Request);

        Assert.False(response.Succeeded);
        Assert.Equal("content_filtered", response.Diagnostics.ErrorCode);
    }

    [Fact]
    public async Task Malformed200_MapsToParseErrorNotThrow()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, "<html><body>504 Gateway Timeout</body></html>");
        var response = await OpenAiClient(handler).GenerateJsonAsync(Request);

        Assert.False(response.Succeeded);
        Assert.Equal("parse_error", response.Diagnostics.ErrorCode);
    }

    [Fact]
    public async Task UnexpectedException_MapsToProviderErrorNotThrow()
    {
        var handler = new ThrowingHandler(new InvalidOperationException("resilience pipeline timed out"));
        var response = await OpenAiClient(handler).GenerateJsonAsync(Request);

        Assert.False(response.Succeeded);
        Assert.Equal("provider_error", response.Diagnostics.ErrorCode);
    }

    [Fact]
    public async Task Ok_CostIncludesReasoningTokens()
    {
        // gpt-5.4-nano: 120 in · 40 out · 32 reasoning → (120·0.20 + (40+32)·1.25) / 1e6.
        var handler = new CapturingHandler(HttpStatusCode.OK, OkBody);
        var response = await OpenAiClient(handler).GenerateJsonAsync(Request);

        var expected = (120 * 0.20m + (40 + 32) * 1.25m) / 1_000_000m;
        Assert.Equal(expected, response.Diagnostics.CostUsd);
    }
}
