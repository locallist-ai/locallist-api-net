namespace LocalList.API.NET.Features.Chat.Services;

/// <summary>
/// Layer 3 guardrail: per-session suspicion accumulator.
/// Defeats multi-turn buildup attacks where each individual message looks innocent
/// but together they establish adversarial context. Persisted in ChatSession.SuspicionJson.
/// </summary>
public class SuspicionTracker
{
    // Score thresholds
    public const int QuarantineThreshold = 80;
    public const int SuppressGeminiThreshold = 50;

    /// <summary>
    /// Mínimo de intentos de injection genuinos para quarantinear. Evita el falso
    /// positivo de un usuario normal que acumula score con preguntas off-topic (benignas,
    /// +10) y una sola frase que dispara un patrón de injection: off-topic NUNCA debe, por
    /// sí solo o con un único falso positivo, quarantinear. Un ataque real de injection
    /// (3 patrones → 90) sigue quarantineándose igual que antes.
    /// </summary>
    public const int MinInjectionsToQuarantine = 2;

    // Score deltas
    public const int InjectionDelta = 30;
    public const int OffTopicDelta = 10;
    public const int DriftDelta = 20;
    public const int CanaryLeakDelta = 100;
    public const int CleanTurnDecay = -5;

    public int Score { get; set; }
    public int InjectionAttempts { get; set; }
    public int OffTopicCount { get; set; }
    public int DriftCount { get; set; }
    public int CanaryLeaks { get; set; }
    public DateTimeOffset? FirstSuspicionAt { get; set; }
    public string? LastTrigger { get; set; }

    public void RecordInjection(string pattern)
    {
        Score += InjectionDelta;
        InjectionAttempts++;
        FirstSuspicionAt ??= DateTimeOffset.UtcNow;
        LastTrigger = $"injection:{pattern[..Math.Min(40, pattern.Length)]}";
    }

    public void RecordOffTopic()
    {
        Score += OffTopicDelta;
        OffTopicCount++;
        FirstSuspicionAt ??= DateTimeOffset.UtcNow;
        LastTrigger = "offtopic";
    }

    public void RecordDrift(string kind)
    {
        Score += DriftDelta;
        DriftCount++;
        FirstSuspicionAt ??= DateTimeOffset.UtcNow;
        LastTrigger = $"drift:{kind}";
    }

    public void RecordCanaryLeak()
    {
        Score += CanaryLeakDelta;
        CanaryLeaks++;
        FirstSuspicionAt ??= DateTimeOffset.UtcNow;
        LastTrigger = "canary_leak";
    }

    public void RecordCleanTurn()
    {
        Score = Math.Max(0, Score + CleanTurnDecay);
    }

    /// <summary>
    /// Quarantine ante una fuga de canary (señal definitiva: el modelo reflejó el system
    /// prompt) o ante injection sostenida (score alto Y al menos
    /// <see cref="MinInjectionsToQuarantine"/> intentos genuinos). El off-topic sube el
    /// score (suprime Gemini para ahorrar tokens) pero no quarantinea por sí solo.
    /// </summary>
    public bool ShouldQuarantine =>
        CanaryLeaks > 0
        || (Score >= QuarantineThreshold && InjectionAttempts >= MinInjectionsToQuarantine);

    public bool ShouldSuppressGemini => Score >= SuppressGeminiThreshold;
}
