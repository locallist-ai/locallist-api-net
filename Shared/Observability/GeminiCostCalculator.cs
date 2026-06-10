namespace LocalList.API.NET.Shared.Observability;

/// <summary>
/// Legacy: usado por los servicios solo-Gemini (translator, descriptions).
/// Delega en <see cref="LlmCostCalculator"/>; los servicios multi-proveedor usan ese directamente.
/// </summary>
public static class GeminiCostCalculator
{
    public static decimal? Calculate(int? inputTokens, int? outputTokens) =>
        LlmCostCalculator.Calculate("gemini-2.5-flash", inputTokens, outputTokens);
}
