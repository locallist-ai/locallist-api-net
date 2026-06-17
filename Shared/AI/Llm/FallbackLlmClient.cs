using System.Text.Json;
using LocalList.API.NET.Shared.Observability;

namespace LocalList.API.NET.Shared.AI.Llm;

/// <summary>
/// Encadena providers en orden: si el actual falla (HTTP, timeout, filtrado o JSON
/// imparseable), prueba el siguiente. Providers con circuito abierto se saltan sin
/// pagar su timeout. El texto devuelto ya viene limpio de fences markdown.
/// Si toda la cadena falla, devuelve los diagnostics del último intento con el
/// resumen de intentos previos en ErrorMessage.
/// </summary>
public sealed class FallbackLlmClient(
    IReadOnlyList<ILlmClient> chain,
    LlmProviderHealthRegistry health,
    ILogger<FallbackLlmClient> logger) : ILlmClient
{
    public string ProviderName => "fallback-chain";

    /// <summary>Modelo nominal del provider primario — quién respondió de verdad va en Diagnostics.Model.</summary>
    public string Model => chain.Count > 0 ? chain[0].Model : string.Empty;

    public IReadOnlyList<ILlmClient> Chain => chain;

    public async Task<LlmJsonResponse> GenerateJsonAsync(LlmJsonRequest request, CancellationToken ct = default)
    {
        if (chain.Count == 0)
        {
            return new LlmJsonResponse(null, new AiCallDiagnostics(
                Provider: "none", Model: "none",
                Prompt: request.Prompt.Length <= 4096 ? request.Prompt : request.Prompt[..4096],
                ResponseRaw: null, FinishReason: null, LatencyMs: 0,
                InputTokens: null, OutputTokens: null, ThinkingTokens: null, TotalTokens: null,
                CostUsd: null, HttpStatus: null, ErrorCode: "missing_key",
                ErrorMessage: "No LLM providers configured (missing API keys)"));
        }

        var attemptErrors = new List<string>();
        LlmJsonResponse? lastResponse = null;
        var attempt = 0;

        foreach (var provider in chain)
        {
            ct.ThrowIfCancellationRequested();

            if (health.IsOpen(provider.ProviderName))
            {
                logger.LogWarning("LLM chain: skipping {Provider} (circuit open)", provider.ProviderName);
                attemptErrors.Add($"{provider.ProviderName}: skipped(circuit_open)");
                continue;
            }

            attempt++;
            LlmJsonResponse response;
            try
            {
                response = await provider.GenerateJsonAsync(request, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Cancelación del caller (timeout del turno) — propagar, no degradar a fallback.
                throw;
            }
            catch (Exception ex)
            {
                // Defensa en profundidad: un provider que lanza (bug, Polly TimeoutRejectedException
                // que se escapara, etc.) no debe abortar la cadena — se cuenta como fallo y se sigue.
                health.RecordFailure(provider.ProviderName);
                attemptErrors.Add($"{provider.ProviderName}: provider_error({ex.GetType().Name})");
                logger.LogWarning(ex, "LLM chain: {Provider} threw, trying next", provider.ProviderName);
                lastResponse = ProviderThrew(request, provider, ex);
                continue;
            }

            if (response.Succeeded && TryCleanJson(response.Text!, out var cleaned))
            {
                health.RecordSuccess(provider.ProviderName);
                var diag = response.Diagnostics with { Attempt = attempt };
                if (attempt > 1)
                {
                    logger.LogWarning("LLM chain: {Provider} answered after failures: {Errors}",
                        provider.ProviderName, string.Join("; ", attemptErrors));
                }
                return new LlmJsonResponse(cleaned, diag);
            }

            health.RecordFailure(provider.ProviderName);
            var error = response.Diagnostics.ErrorCode ?? "invalid_json";
            attemptErrors.Add($"{provider.ProviderName}: {error}({response.Diagnostics.HttpStatus?.ToString() ?? "-"})");
            logger.LogWarning("LLM chain: {Provider} failed ({Error}), trying next", provider.ProviderName, error);

            lastResponse = response.Succeeded
                ? new LlmJsonResponse(null, response.Diagnostics with { ErrorCode = "parse_error", ErrorMessage = "Model returned non-JSON output" })
                : response;
        }

        var lastDiag = lastResponse?.Diagnostics ?? new AiCallDiagnostics(
            Provider: "none", Model: "none",
            Prompt: request.Prompt.Length <= 4096 ? request.Prompt : request.Prompt[..4096],
            ResponseRaw: null, FinishReason: null, LatencyMs: 0,
            InputTokens: null, OutputTokens: null, ThinkingTokens: null, TotalTokens: null,
            CostUsd: null, HttpStatus: null, ErrorCode: "circuit_open",
            ErrorMessage: "All providers skipped (circuits open)");

        logger.LogError("LLM chain: all providers failed: {Errors}", string.Join("; ", attemptErrors));
        return new LlmJsonResponse(null, lastDiag with
        {
            Attempt = attempt,
            ErrorMessage = string.Join("; ", attemptErrors),
        });
    }

    /// <summary>Diagnostics sintéticos para un provider que lanzó (no devolvió respuesta estructurada).</summary>
    private static LlmJsonResponse ProviderThrew(LlmJsonRequest request, ILlmClient provider, Exception ex) =>
        new(null, new AiCallDiagnostics(
            Provider: provider.ProviderName, Model: provider.Model,
            Prompt: request.Prompt.Length <= 4096 ? request.Prompt : request.Prompt[..4096],
            ResponseRaw: null, FinishReason: null, LatencyMs: 0,
            InputTokens: null, OutputTokens: null, ThinkingTokens: null, TotalTokens: null,
            CostUsd: null, HttpStatus: null, ErrorCode: "provider_error",
            ErrorMessage: $"{ex.GetType().Name}: {ex.Message}"));

    /// <summary>
    /// Limpia fences markdown (```json, ```JSON, ``` con o sin lenguaje) y verifica que el
    /// resultado sea JSON parseable. Tolera \n, \r\n o espacios tras la apertura del fence.
    /// Un modelo que devuelve prosa en vez de JSON cuenta como fallo → siguiente provider.
    /// </summary>
    private static bool TryCleanJson(string raw, out string cleaned)
    {
        cleaned = StripFences(raw);
        try
        {
            using var _ = JsonDocument.Parse(cleaned);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string StripFences(string raw)
    {
        var s = raw.Trim();
        if (!s.StartsWith("```", StringComparison.Ordinal)) return s;

        // Quitar la línea de apertura del fence (```, ```json, ```JSON …) hasta el primer salto.
        var firstBreak = s.IndexOf('\n');
        s = firstBreak >= 0 ? s[(firstBreak + 1)..] : s[3..];

        // Quitar el fence de cierre si existe.
        s = s.TrimEnd();
        if (s.EndsWith("```", StringComparison.Ordinal)) s = s[..^3];

        return s.Trim();
    }
}
