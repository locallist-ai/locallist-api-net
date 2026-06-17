namespace LocalList.API.NET.Shared.Observability;

/// <summary>
/// Diagnóstico normalizado de una llamada LLM, independiente del proveedor.
/// Provider/Model reflejan quién respondió realmente (con fallback puede no ser el primario).
/// Attempt es la posición en la cadena de fallback (1 = primario).
/// HttpStatus es el status crudo del proveedor; se persiste en la columna legacy gemini_status.
/// </summary>
public sealed record AiCallDiagnostics(
    string Provider,
    string Model,
    string Prompt,
    string? ResponseRaw,
    string? FinishReason,
    int LatencyMs,
    int? InputTokens,
    int? OutputTokens,
    int? ThinkingTokens,
    int? TotalTokens,
    decimal? CostUsd,
    int? HttpStatus,
    string? ErrorCode,
    string? ErrorMessage,
    int Attempt = 1
);
