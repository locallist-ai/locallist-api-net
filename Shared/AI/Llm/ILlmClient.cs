using System.Text.Json;
using LocalList.API.NET.Shared.Observability;

namespace LocalList.API.NET.Shared.AI.Llm;

/// <summary>
/// Petición provider-agnostic: prompt completo → JSON.
/// Cada provider traduce Temperature/MaxOutputTokens/JsonSchema a su wire format
/// (o los ignora si su modelo no los soporta, p. ej. temperature en GPT-5 Nano).
/// </summary>
public sealed record LlmJsonRequest(
    string Prompt,
    double Temperature,
    int MaxOutputTokens,
    JsonElement? JsonSchema = null);

/// <summary>
/// Respuesta normalizada. Text es el JSON crudo del modelo (null = fallo).
/// Diagnostics está siempre presente, incluso en fallo, para persistir en chat_turns.
/// </summary>
public sealed record LlmJsonResponse(
    string? Text,
    AiCallDiagnostics Diagnostics)
{
    public bool Succeeded => Text is not null && Diagnostics.ErrorCode is null;
}

public interface ILlmClient
{
    /// <summary>"gemini" | "openai" | "mistral" | "anthropic" | "fallback-chain".</summary>
    string ProviderName { get; }

    string Model { get; }

    Task<LlmJsonResponse> GenerateJsonAsync(LlmJsonRequest request, CancellationToken ct = default);
}
