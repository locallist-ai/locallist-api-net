using LocalList.API.NET.Shared.Observability;

namespace LocalList.API.Tests.Unit.Llm;

public class LlmCostCalculatorTests
{
    [Theory]
    [InlineData("gemini-2.5-flash", 1_000_000, 1_000_000, 2.80)]   // 0.30 + 2.50
    [InlineData("gpt-5.4-nano", 1_000_000, 1_000_000, 1.45)]       // 0.20 + 1.25
    [InlineData("mistral-small-latest", 1_000_000, 1_000_000, 0.40)] // 0.10 + 0.30
    [InlineData("claude-haiku-4-5", 1_000_000, 1_000_000, 6.00)]   // 1.00 + 5.00
    [InlineData("gemini-3.5-flash", 1_000_000, 1_000_000, 10.50)]  // 1.50 + 9.00
    public void Calculate_KnownModels_ReturnsExpectedCost(string model, int input, int output, decimal expected)
    {
        var cost = LlmCostCalculator.Calculate(model, input, output);
        Assert.Equal(expected, cost);
    }

    [Fact]
    public void Calculate_UnknownModel_ReturnsNull()
    {
        Assert.Null(LlmCostCalculator.Calculate("modelo-desconocido", 1000, 1000));
    }

    [Fact]
    public void Calculate_NoTokens_ReturnsNull()
    {
        Assert.Null(LlmCostCalculator.Calculate("gemini-2.5-flash", null, null));
    }

    [Fact]
    public void Calculate_OnlyInputTokens_CostsInputOnly()
    {
        var cost = LlmCostCalculator.Calculate("gemini-2.5-flash", 1_000_000, null);
        Assert.Equal(0.30m, cost);
    }

    [Fact]
    public void Calculate_ThinkingTokens_BilledAtOutputRate()
    {
        // gemini-2.5-flash: thoughtsTokenCount se factura a precio de output (2.50/M).
        // 0 in · 1M out · 1M thinking → 2·2.50 = 5.00.
        var cost = LlmCostCalculator.Calculate("gemini-2.5-flash", null, 1_000_000, 1_000_000);
        Assert.Equal(5.00m, cost);
    }

    [Fact]
    public void Calculate_OnlyThinkingTokens_NotNull()
    {
        // Aunque input y output sean null, si hay thinking tokens hay coste (no null).
        var cost = LlmCostCalculator.Calculate("gpt-5.4-nano", null, null, 1_000_000);
        Assert.Equal(1.25m, cost);
    }

    [Fact]
    public void GeminiCostCalculator_DelegatesToLlmCostCalculator()
    {
        Assert.Equal(
            LlmCostCalculator.Calculate("gemini-2.5-flash", 1234, 567),
            GeminiCostCalculator.Calculate(1234, 567));
    }
}
