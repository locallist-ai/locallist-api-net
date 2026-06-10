using System.Net;
using System.Text.Json;
using LocalList.API.NET.Shared.AI.Llm;
using LocalList.API.NET.Shared.AI.Llm.Providers;
using Microsoft.Extensions.Logging.Abstractions;

namespace LocalList.API.Tests.Unit.Llm;

public class AnthropicLlmClientTests
{
    private static readonly LlmJsonRequest Request = new("extract slots", 0.2, 200);

    private const string OkBody = """
        {
          "content": [{"type": "text", "text": "{\"ok\":true}"}],
          "stop_reason": "end_turn",
          "usage": {"input_tokens": 150, "output_tokens": 60}
        }
        """;

    private static AnthropicLlmClient Client(CapturingHandler handler) =>
        new(new HttpClient(handler), "test-key", "claude-haiku-4-5", NullLogger.Instance);

    [Fact]
    public async Task RequestShape_UsesAnthropicHeadersAndBody()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, OkBody);
        await Client(handler).GenerateJsonAsync(Request);

        Assert.Equal("https://api.anthropic.com/v1/messages", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("test-key", handler.LastRequest.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", handler.LastRequest.Headers.GetValues("anthropic-version").Single());

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var root = body.RootElement;
        Assert.Equal("claude-haiku-4-5", root.GetProperty("model").GetString());
        Assert.Equal(200, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal(0.2, root.GetProperty("temperature").GetDouble());
        // Sin JsonSchema en la request no se envía output_config (JSON pedido por prompt).
        Assert.False(root.TryGetProperty("output_config", out _));
    }

    [Fact]
    public async Task JsonSchema_AddsOutputConfigFormat()
    {
        using var schemaDoc = JsonDocument.Parse(
            """{"type":"object","properties":{"ok":{"type":"boolean"}},"additionalProperties":false}""");
        var request = Request with { JsonSchema = schemaDoc.RootElement.Clone() };

        var handler = new CapturingHandler(HttpStatusCode.OK, OkBody);
        await Client(handler).GenerateJsonAsync(request);

        using var body = JsonDocument.Parse(handler.LastRequestBody!);
        var format = body.RootElement.GetProperty("output_config").GetProperty("format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.Equal("object", format.GetProperty("schema").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Ok_ParsesTextTokensAndCost()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, OkBody);
        var response = await Client(handler).GenerateJsonAsync(Request);

        Assert.True(response.Succeeded);
        Assert.Equal("{\"ok\":true}", response.Text);
        Assert.Equal("anthropic", response.Diagnostics.Provider);
        Assert.Equal("claude-haiku-4-5", response.Diagnostics.Model);
        Assert.Equal(150, response.Diagnostics.InputTokens);
        Assert.Equal(60, response.Diagnostics.OutputTokens);
        Assert.Equal("end_turn", response.Diagnostics.FinishReason);
        Assert.NotNull(response.Diagnostics.CostUsd);
    }

    [Fact]
    public async Task Refusal_MapsToContentFiltered()
    {
        var body = """
            {"content": [{"type": "text", "text": "I cannot help with that."}],
             "stop_reason": "refusal",
             "usage": {"input_tokens": 100, "output_tokens": 10}}
            """;
        var handler = new CapturingHandler(HttpStatusCode.OK, body);
        var response = await Client(handler).GenerateJsonAsync(Request);

        Assert.False(response.Succeeded);
        Assert.Equal("content_filtered", response.Diagnostics.ErrorCode);
    }

    [Fact]
    public async Task HttpError_ReturnsFailureWithStatus()
    {
        var handler = new CapturingHandler(HttpStatusCode.InternalServerError, "{\"type\":\"error\"}");
        var response = await Client(handler).GenerateJsonAsync(Request);

        Assert.False(response.Succeeded);
        Assert.Equal("http_error", response.Diagnostics.ErrorCode);
        Assert.Equal(500, response.Diagnostics.HttpStatus);
    }
}
