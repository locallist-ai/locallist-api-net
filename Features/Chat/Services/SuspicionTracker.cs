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
        FirstSuspicionAt ??= DateTimeOffset.UtcNow;
        LastTrigger = "canary_leak";
    }

    public void RecordCleanTurn()
    {
        Score = Math.Max(0, Score + CleanTurnDecay);
    }

    public bool ShouldQuarantine => Score >= QuarantineThreshold;
    public bool ShouldSuppressGemini => Score >= SuppressGeminiThreshold;
}
