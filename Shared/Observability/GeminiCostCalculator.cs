namespace LocalList.API.NET.Shared.Observability;

public static class GeminiCostCalculator
{
    // Gemini 2.5 Flash pricing (per 1M tokens, as of 2026-05)
    private const decimal InputPricePerMillion = 0.30m;
    private const decimal OutputPricePerMillion = 2.50m;

    public static decimal? Calculate(int? inputTokens, int? outputTokens)
    {
        if (inputTokens == null && outputTokens == null) return null;
        var inputCost = (inputTokens ?? 0) * InputPricePerMillion / 1_000_000m;
        var outputCost = (outputTokens ?? 0) * OutputPricePerMillion / 1_000_000m;
        return inputCost + outputCost;
    }
}
