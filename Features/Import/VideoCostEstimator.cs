namespace LocalList.API.NET.Features.Import;

/// <summary>
/// Diagnóstico de coste del import de vídeo. Los tokens reales los devuelve
/// <c>usageMetadata.promptTokenCount</c> (Gemini ya cuenta el vídeo dentro del prompt),
/// y el coste se calcula con <c>LlmCostCalculator</c> sobre esos tokens reales. Esta clase
/// aporta la parte <b>estimable a priori</b>: cuántos tokens consumirá un vídeo de N segundos,
/// útil para dimensionar coste antes de subir y para contrastar con el usageMetadata real.
///
/// Ratios verificados el 2026-07-23 contra la doc oficial de Gemini
/// (ai.google.dev/gemini-api/docs/video-understanding y .../tokens):
///   - Vídeo a resolución media (muestreo 1 FPS): <b>258 tokens por segundo</b> (258 tok/frame).
///   - Pista de audio: <b>32 tokens por segundo</b>.
///   → ~290 tok/s ≈ ~17.4K tokens/min.
///
/// Pricing verificado el mismo día (ai.google.dev/gemini-api/docs/pricing) para gemini-3.1-flash:
///   input $0.25/1M (text/image/<b>video</b>), input audio $0.50/1M, output $1.50/1M.
///   Un vídeo de 60s ≈ 17.4K tokens de entrada ≈ $0.0045 de input + el output del JSON
///   (unos pocos miles de tokens ≈ $0.002-0.01) → ~$0.005-0.01 por import. Coherente con el brief.
///
/// Nota de precisión: el audio se factura a $0.50/1M (2× el vídeo). <c>LlmCostCalculator</c>
/// usa una tarifa de input plana ($0.25) para gemini-3.1-flash, así que infravalora el coste
/// del audio (~11% de los tokens de media) en ~5%. Es un sesgo conocido y acotado; el coste
/// persistido sale de los tokens reales, y aquí exponemos el desglose de media para auditarlo.
/// </summary>
public static class VideoCostEstimator
{
    public const int VideoTokensPerSecond = 258;
    public const int AudioTokensPerSecond = 32;

    public sealed record MediaTokenEstimate(int VideoTokens, int AudioTokens, int TotalMediaTokens);

    /// <summary>Estima los tokens de media (vídeo + audio) para un vídeo de la duración dada.</summary>
    public static MediaTokenEstimate EstimateMediaTokens(double durationSec)
    {
        var seconds = double.IsNaN(durationSec) ? 0 : Math.Max(0, durationSec);
        // Clamp en el dominio double ANTES del cast: `(int)(seconds * 258)` con duración enorme
        // hace wrap a negativo (overflow del cast). Saturamos a int.MaxValue en su lugar.
        var video = ClampToInt(seconds * VideoTokensPerSecond);
        var audio = ClampToInt(seconds * AudioTokensPerSecond);
        var total = ClampToInt((double)video + audio);
        return new MediaTokenEstimate(video, audio, total);
    }

    private static int ClampToInt(double value)
    {
        if (double.IsNaN(value) || value <= 0) return 0;
        if (value >= int.MaxValue) return int.MaxValue;
        return (int)Math.Round(value, MidpointRounding.AwayFromZero);
    }
}
