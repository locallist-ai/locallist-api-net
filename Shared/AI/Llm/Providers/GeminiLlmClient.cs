using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LocalList.API.NET.Shared.Observability;

namespace LocalList.API.NET.Shared.AI.Llm.Providers;

/// <summary>
/// Provider Gemini (generativelanguage.googleapis.com, generateContent).
/// Extraído del bloque HTTP que vivía duplicado en SlotExtractorService y
/// PreferenceExtractorService; conserva su manejo de usageMetadata, finishReason
/// y parts vacíos por SAFETY.
/// </summary>
public sealed class GeminiLlmClient(
    HttpClient httpClient,
    string apiKey,
    string model,
    ILogger logger,
    int minOutputTokens = 0) : ILlmClient
{
    public string ProviderName => "gemini";
    public string Model => model;

    public async Task<LlmJsonResponse> GenerateJsonAsync(LlmJsonRequest request, CancellationToken ct = default)
    {
        // gemini-2.5-flash trae thinking ON por defecto y los thinking-tokens cuentan
        // contra maxOutputTokens: con budgets pequeños el JSON sale truncado
        // (finishReason=MAX_TOKENS). thinkingBudget=0 desactiva el razonamiento (slot y
        // preference extraction no lo necesitan) y el suelo minOutputTokens da holgura,
        // espejo del reasoning_effort:minimal + minOutputTokens del cliente OpenAI.
        var maxTokens = Math.Max(request.MaxOutputTokens, minOutputTokens);
        var thinkingConfig = new { thinkingBudget = 0 };

        object generationConfig = request.JsonSchema is { } schema
            ? new
            {
                temperature = request.Temperature,
                maxOutputTokens = maxTokens,
                responseMimeType = "application/json",
                responseSchema = schema,
                thinkingConfig,
            }
            : new
            {
                temperature = request.Temperature,
                maxOutputTokens = maxTokens,
                responseMimeType = "application/json",
                thinkingConfig,
            };

        var requestBody = new
        {
            contents = new[] { new { parts = new[] { new { text = request.Prompt } } } },
            generationConfig,
        };

        var sw = Stopwatch.StartNew();
        string? rawBody = null;
        try
        {
            var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent";
            var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
            httpRequest.Headers.Add("x-goog-api-key", apiKey);
            httpRequest.Content = content;

            var response = await httpClient.SendAsync(httpRequest, ct);
            sw.Stop();
            var latencyMs = (int)sw.ElapsedMilliseconds;

            var responseJson = await response.Content.ReadAsStringAsync(ct);
            rawBody = LlmDiagnostics.TruncateResponse(responseJson);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("LLM[gemini]: API returned {Status}", (int)response.StatusCode);
                return Failure(request, LlmDiagnostics.TruncateResponse(responseJson), latencyMs,
                    (int)response.StatusCode, "http_error", $"HTTP {(int)response.StatusCode}");
            }

            using var doc = JsonDocument.Parse(responseJson);

            int? inputTokens = null, outputTokens = null, thinkingTokens = null;
            string? finishReason = null;
            if (doc.RootElement.TryGetProperty("usageMetadata", out var usage))
            {
                if (usage.TryGetProperty("promptTokenCount", out var ptc)) inputTokens = ptc.GetInt32();
                if (usage.TryGetProperty("candidatesTokenCount", out var ctc)) outputTokens = ctc.GetInt32();
                if (usage.TryGetProperty("thoughtsTokenCount", out var ttc)) thinkingTokens = ttc.GetInt32();
            }
            if (doc.RootElement.TryGetProperty("candidates", out var cands) && cands.GetArrayLength() > 0)
            {
                if (cands[0].TryGetProperty("finishReason", out var fr)) finishReason = fr.GetString();
            }

            var totalTokens = (inputTokens ?? 0) + (outputTokens ?? 0) + (thinkingTokens ?? 0);

            // Truncación: MAX_TOKENS deja el JSON cortado. Devolver un fallo explícito
            // "truncated" (en vez de dejar que TryCleanJson lo reporte como invalid_json
            // genérico) hace el diagnóstico legible y deja claro que el siguiente provider
            // debe intentarlo. Con thinkingBudget=0 + suelo de tokens esto ya no debería
            // dispararse, pero queda como defensa.
            if (finishReason == "MAX_TOKENS")
            {
                logger.LogWarning(
                    "LLM[gemini]: response truncated (MAX_TOKENS) out={Out} thinking={Think} max={Max}",
                    outputTokens, thinkingTokens, maxTokens);
                return Failure(request, LlmDiagnostics.TruncateResponse(responseJson), latencyMs,
                    (int)response.StatusCode, "truncated",
                    $"Response truncated (finishReason=MAX_TOKENS, outputTokens={outputTokens}, thinkingTokens={thinkingTokens})",
                    finishReason, inputTokens, outputTokens, thinkingTokens);
            }

            // parts puede venir vacío en respuestas filtradas por SAFETY.
            string? text = null;
            if (doc.RootElement.TryGetProperty("candidates", out var candidates)
                && candidates.GetArrayLength() > 0
                && candidates[0].TryGetProperty("content", out var candContent)
                && candContent.TryGetProperty("parts", out var parts)
                && parts.GetArrayLength() > 0
                && parts[0].TryGetProperty("text", out var t))
            {
                text = t.GetString();
            }

            if (text is null)
            {
                logger.LogWarning("LLM[gemini]: empty parts (finishReason={Reason})", finishReason);
                return Failure(request, LlmDiagnostics.TruncateResponse(responseJson), latencyMs,
                    (int)response.StatusCode, "content_filtered", $"Empty response (finishReason={finishReason})",
                    finishReason, inputTokens, outputTokens, thinkingTokens);
            }

            var diag = new AiCallDiagnostics(
                Provider: ProviderName,
                Model: model,
                Prompt: LlmDiagnostics.TruncatePrompt(request.Prompt),
                ResponseRaw: LlmDiagnostics.TruncateResponse(responseJson),
                FinishReason: finishReason,
                LatencyMs: latencyMs,
                InputTokens: inputTokens,
                OutputTokens: outputTokens,
                ThinkingTokens: thinkingTokens,
                TotalTokens: totalTokens > 0 ? totalTokens : null,
                CostUsd: LlmCostCalculator.Calculate(model, inputTokens, outputTokens, thinkingTokens),
                HttpStatus: (int)response.StatusCode,
                ErrorCode: null,
                ErrorMessage: null);

            return new LlmJsonResponse(text, diag);
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            logger.LogError(ex, "LLM[gemini]: API call failed");
            return Failure(request, rawBody, (int)sw.ElapsedMilliseconds, null, "http_error", ex.Message);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancelación del caller (timeout del turno) — propagar, no es fallo del provider.
            throw;
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            logger.LogError("LLM[gemini]: API call timed out");
            return Failure(request, rawBody, (int)sw.ElapsedMilliseconds, null, "timeout", "Gemini API call timed out");
        }
        catch (JsonException ex)
        {
            // 200 con cuerpo no-JSON (HTML de un proxy/gateway, body truncado): fallo del provider, no excepción.
            sw.Stop();
            logger.LogError(ex, "LLM[gemini]: malformed JSON response body");
            return Failure(request, rawBody, (int)sw.ElapsedMilliseconds, null, "parse_error", ex.Message);
        }
        catch (Exception ex)
        {
            // Polly TimeoutRejectedException (AttemptTimeout/TotalRequestTimeout) y cualquier otro
            // fallo inesperado: convertir en fallo del provider para que la cadena pruebe el siguiente.
            sw.Stop();
            logger.LogError(ex, "LLM[gemini]: unexpected error ({Type})", ex.GetType().Name);
            return Failure(request, rawBody, (int)sw.ElapsedMilliseconds, null, "provider_error", ex.Message);
        }
    }

    private LlmJsonResponse Failure(
        LlmJsonRequest request, string? responseRaw, int latencyMs, int? httpStatus,
        string errorCode, string errorMessage, string? finishReason = null,
        int? inputTokens = null, int? outputTokens = null, int? thinkingTokens = null) =>
        new(null, new AiCallDiagnostics(
            Provider: ProviderName, Model: model,
            Prompt: LlmDiagnostics.TruncatePrompt(request.Prompt),
            ResponseRaw: responseRaw, FinishReason: finishReason, LatencyMs: latencyMs,
            InputTokens: inputTokens, OutputTokens: outputTokens, ThinkingTokens: thinkingTokens, TotalTokens: null,
            CostUsd: null, HttpStatus: httpStatus, ErrorCode: errorCode, ErrorMessage: errorMessage));
}
