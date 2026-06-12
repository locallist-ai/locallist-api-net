namespace LocalList.API.NET.Shared.Observability;

/// <summary>
/// Coste por tokens según (provider, model). Precios por 1M tokens, verificados
/// contra el pricing oficial de cada provider el 2026-06-12
/// (ai.google.dev/gemini-api/docs/pricing, openai.com/api/pricing,
/// mistral.ai/pricing, platform.claude.com docs).
/// Modelo desconocido → null (no inventar costes); el caller loggea el warning.
/// </summary>
public static class LlmCostCalculator
{
    private static readonly Dictionary<string, (decimal InPerM, decimal OutPerM)> Prices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gemini-2.5-flash"]      = (0.30m, 2.50m),
        ["gemini-3.5-flash"]      = (1.50m, 9.00m),
        ["gemini-3.1-flash-lite"] = (0.25m, 1.50m),
        ["gpt-5-nano"]            = (0.05m, 0.40m),
        ["mistral-small-latest"]  = (0.10m, 0.30m),
        ["claude-haiku-4-5"]      = (1.00m, 5.00m),
    };

    public static decimal? Calculate(string model, int? inputTokens, int? outputTokens)
    {
        if (inputTokens == null && outputTokens == null) return null;
        if (!Prices.TryGetValue(model, out var price)) return null;
        var inputCost = (inputTokens ?? 0) * price.InPerM / 1_000_000m;
        var outputCost = (outputTokens ?? 0) * price.OutPerM / 1_000_000m;
        return inputCost + outputCost;
    }

    public static bool HasPricing(string model) => Prices.ContainsKey(model);
}
