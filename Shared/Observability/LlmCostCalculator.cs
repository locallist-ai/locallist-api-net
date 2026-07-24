namespace LocalList.API.NET.Shared.Observability;

/// <summary>
/// Coste por tokens según (provider, model). Precios por 1M tokens, verificados
/// contra el pricing oficial de cada provider el 2026-07-23
/// (ai.google.dev/gemini-api/docs/pricing, developers.openai.com/api/docs/pricing,
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
        ["gpt-5.4-nano"]          = (0.20m, 1.25m),
        ["mistral-small-latest"]  = (0.10m, 0.30m),
        ["claude-haiku-4-5"]      = (1.00m, 5.00m),
    };

    /// <summary>
    /// Coste = input·InPerM + (output + thinking)·OutPerM. Los tokens de razonamiento
    /// (thoughtsTokenCount en Gemini 2.5, reasoning_tokens en gpt-5.4-nano) se facturan a
    /// precio de output: omitirlos infravalora sistemáticamente el coste cuando el modelo razona.
    /// </summary>
    public static decimal? Calculate(string model, int? inputTokens, int? outputTokens, int? thinkingTokens = null)
    {
        if (inputTokens == null && outputTokens == null && thinkingTokens == null) return null;
        if (!Prices.TryGetValue(model, out var price)) return null;
        var inputCost = (inputTokens ?? 0) * price.InPerM / 1_000_000m;
        var outputCost = ((outputTokens ?? 0) + (thinkingTokens ?? 0)) * price.OutPerM / 1_000_000m;
        return inputCost + outputCost;
    }

    public static bool HasPricing(string model) => Prices.ContainsKey(model);
}
