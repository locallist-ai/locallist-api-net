using System.Text.Json;
using LocalList.API.NET.Shared.AI.Services;

namespace LocalList.API.Tests.Unit;

// Regression guard for the PROD outage that blocked the Spanish-content rollout:
// gemini-2.5-flash has chain-of-thought thinking ON by default and the thinking-tokens
// count against maxOutputTokens, so the translator's ES JSON came back truncated
// (finishReason=MAX_TOKENS) and failed to parse (translated=0 failed=10). The fix mirrors
// GeminiLlmClient: thinkingConfig.thinkingBudget=0 + a generous maxOutputTokens. These tests
// assert the serialized generationConfig so a future refactor dropping thinkingBudget (which
// would silently reintroduce the outage) fails loudly here instead of only in prod.
public class PlaceTranslatorServiceTests
{
    private static JsonElement GenerationConfig(int maxOutputTokens)
    {
        var body = PlaceTranslatorService.BuildTranslationRequestBody("prompt text", maxOutputTokens);
        // Round-trip through the exact serializer the service uses so we assert the wire shape.
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(body));
        return doc.RootElement.GetProperty("generationConfig").Clone();
    }

    [Fact]
    public void PlaceTranslation_DisablesThinking_WithThinkingBudgetZero()
    {
        var gc = GenerationConfig(2048);
        var thinking = gc.GetProperty("thinkingConfig");
        Assert.Equal(0, thinking.GetProperty("thinkingBudget").GetInt32());
    }

    [Fact]
    public void PlaceTranslation_UsesRaisedMaxOutputTokens()
    {
        var gc = GenerationConfig(2048);
        Assert.Equal(2048, gc.GetProperty("maxOutputTokens").GetInt32());
    }

    [Fact]
    public void PlanTranslation_DisablesThinking_WithThinkingBudgetZero()
    {
        var gc = GenerationConfig(1024);
        var thinking = gc.GetProperty("thinkingConfig");
        Assert.Equal(0, thinking.GetProperty("thinkingBudget").GetInt32());
    }

    [Fact]
    public void PlanTranslation_UsesRaisedMaxOutputTokens()
    {
        var gc = GenerationConfig(1024);
        Assert.Equal(1024, gc.GetProperty("maxOutputTokens").GetInt32());
    }

    [Fact]
    public void RequestBody_KeepsJsonMimeTypeAndLowTemperature()
    {
        // The rest of the config must survive the fix unchanged (JSON output, deterministic tone).
        var gc = GenerationConfig(2048);
        Assert.Equal("application/json", gc.GetProperty("responseMimeType").GetString());
        Assert.Equal(0.2, gc.GetProperty("temperature").GetDouble());
    }

    [Fact]
    public void RequestBody_CarriesPromptInContents()
    {
        var body = PlaceTranslatorService.BuildTranslationRequestBody("translate this", 2048);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(body));
        var text = doc.RootElement
            .GetProperty("contents")[0]
            .GetProperty("parts")[0]
            .GetProperty("text").GetString();
        Assert.Equal("translate this", text);
    }
}
