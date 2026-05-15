namespace LocalList.API.NET.Shared.Observability;

public sealed record AiCallDiagnostics(
    string Prompt,
    string? ResponseRaw,
    string? FinishReason,
    int LatencyMs,
    int? InputTokens,
    int? OutputTokens,
    int? ThinkingTokens,
    int? TotalTokens,
    decimal? CostUsd,
    int? GeminiStatus,
    string? ErrorCode,
    string? ErrorMessage
);
