using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LocalList.API.NET.Shared.Observability;

namespace LocalList.API.NET.Shared.AI.Llm.Providers;

/// <summary>
/// Provider para APIs OpenAI-compatible (chat/completions): OpenAI, Mistral, DeepSeek…
/// Parametrizado por baseUrl/model/flags porque los dialectos difieren en detalles:
/// GPT-5 usa max_completion_tokens y rechaza temperature≠1; Mistral usa max_tokens clásico.
/// v1 usa response_format json_object (no json_schema: los dialectos de schema difieren
/// entre vendors); la validación semántica vive en los callers.
/// </summary>
public sealed class OpenAiCompatibleLlmClient(
    HttpClient httpClient,
    string apiKey,
    string providerName,
    string baseUrl,
    string model,
    ILogger logger,
    bool usesMaxCompletionTokens = false,
    bool supportsTemperature = true,
    int minOutputTokens = 0,
    string? reasoningEffort = null) : ILlmClient
{
    public string ProviderName => providerName;
    public string Model => model;

    public async Task<LlmJsonResponse> GenerateJsonAsync(LlmJsonRequest request, CancellationToken ct = default)
    {
        // Los modelos razonadores gastan reasoning tokens dentro del budget de salida:
        // con budgets pequeños (200) la respuesta sale vacía. Forzar un mínimo.
        var maxTokens = Math.Max(request.MaxOutputTokens, minOutputTokens);

        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = new[] { new { role = "user", content = request.Prompt } },
            // json_object exige que la palabra "json" aparezca en el prompt o OpenAI responde 400.
            // Invariante garantizada por los callers (slot/preference extraction lo mencionan explícitamente).
            ["response_format"] = new { type = "json_object" },
            [usesMaxCompletionTokens ? "max_completion_tokens" : "max_tokens"] = maxTokens,
        };
        if (supportsTemperature) body["temperature"] = request.Temperature;
        if (reasoningEffort != null) body["reasoning_effort"] = reasoningEffort;

        var sw = Stopwatch.StartNew();
        string? rawBody = null;
        try
        {
            var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/chat/completions");
            httpRequest.Headers.Add("Authorization", $"Bearer {apiKey}");
            httpRequest.Content = content;

            var response = await httpClient.SendAsync(httpRequest, ct);
            sw.Stop();
            var latencyMs = (int)sw.ElapsedMilliseconds;

            var responseJson = await response.Content.ReadAsStringAsync(ct);
            rawBody = LlmDiagnostics.TruncateResponse(responseJson);

            if (!response.IsSuccessStatusCode)
            {
                // Body redactado + recortado en ErrorMessage para diagnóstico admin (cuota/429,
                // etc.); el log se queda en el status. La API key va en headers, no en el body.
                logger.LogError("LLM[{Provider}]: API returned {Status}", providerName, (int)response.StatusCode);
                var errorBody = PiiRedactor.Redact(LlmDiagnostics.TruncateErrorBody(responseJson));
                return Failure(request, LlmDiagnostics.TruncateResponse(responseJson), latencyMs,
                    (int)response.StatusCode, "http_error", $"HTTP {(int)response.StatusCode}: {errorBody}");
            }

            using var doc = JsonDocument.Parse(responseJson);

            int? inputTokens = null, outputTokens = null, thinkingTokens = null;
            if (doc.RootElement.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("prompt_tokens", out var pt)) inputTokens = pt.GetInt32();
                if (usage.TryGetProperty("completion_tokens", out var ot)) outputTokens = ot.GetInt32();
                if (usage.TryGetProperty("completion_tokens_details", out var details)
                    && details.TryGetProperty("reasoning_tokens", out var rt))
                {
                    thinkingTokens = rt.GetInt32();
                }
            }

            string? finishReason = null;
            string? text = null;
            if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
            {
                var choice = choices[0];
                if (choice.TryGetProperty("finish_reason", out var fr)) finishReason = fr.GetString();
                if (choice.TryGetProperty("message", out var msg)
                    && msg.TryGetProperty("content", out var c)
                    && c.ValueKind == JsonValueKind.String)
                {
                    text = c.GetString();
                }
            }

            if (string.IsNullOrEmpty(text))
            {
                var code = finishReason is "content_filter" ? "content_filtered" : "empty_response";
                logger.LogWarning("LLM[{Provider}]: empty content (finish_reason={Reason})", providerName, finishReason);
                return Failure(request, LlmDiagnostics.TruncateResponse(responseJson), latencyMs,
                    (int)response.StatusCode, code, $"Empty response (finish_reason={finishReason})",
                    finishReason, inputTokens, outputTokens, thinkingTokens);
            }

            var totalTokens = (inputTokens ?? 0) + (outputTokens ?? 0);

            var diag = new AiCallDiagnostics(
                Provider: providerName,
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
            logger.LogError(ex, "LLM[{Provider}]: API call failed", providerName);
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
            logger.LogError("LLM[{Provider}]: API call timed out", providerName);
            return Failure(request, rawBody, (int)sw.ElapsedMilliseconds, null, "timeout", $"{providerName} API call timed out");
        }
        catch (JsonException ex)
        {
            // 200 con cuerpo no-JSON (HTML de un proxy/gateway, body truncado): fallo del provider, no excepción.
            sw.Stop();
            logger.LogError(ex, "LLM[{Provider}]: malformed JSON response body", providerName);
            return Failure(request, rawBody, (int)sw.ElapsedMilliseconds, null, "parse_error", ex.Message);
        }
        catch (Exception ex)
        {
            // Polly TimeoutRejectedException (AttemptTimeout/TotalRequestTimeout) y cualquier otro
            // fallo inesperado: convertir en fallo del provider para que la cadena pruebe el siguiente.
            sw.Stop();
            logger.LogError(ex, "LLM[{Provider}]: unexpected error ({Type})", providerName, ex.GetType().Name);
            return Failure(request, rawBody, (int)sw.ElapsedMilliseconds, null, "provider_error", ex.Message);
        }
    }

    private LlmJsonResponse Failure(
        LlmJsonRequest request, string? responseRaw, int latencyMs, int? httpStatus,
        string errorCode, string errorMessage, string? finishReason = null,
        int? inputTokens = null, int? outputTokens = null, int? thinkingTokens = null) =>
        new(null, new AiCallDiagnostics(
            Provider: providerName, Model: model,
            Prompt: LlmDiagnostics.TruncatePrompt(request.Prompt),
            ResponseRaw: responseRaw, FinishReason: finishReason, LatencyMs: latencyMs,
            InputTokens: inputTokens, OutputTokens: outputTokens, ThinkingTokens: thinkingTokens, TotalTokens: null,
            CostUsd: null, HttpStatus: httpStatus, ErrorCode: errorCode, ErrorMessage: errorMessage));
}
