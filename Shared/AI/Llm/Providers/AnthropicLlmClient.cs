using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalList.API.NET.Shared.Observability;

namespace LocalList.API.NET.Shared.AI.Llm.Providers;

/// <summary>
/// Provider Anthropic (POST /v1/messages, Claude Haiku 4.5).
/// Structured output vía output_config.format si la request trae JsonSchema.
/// El schema se comparte con Gemini (responseSchema), cuyo dialecto es más laxo:
/// Anthropic exige additionalProperties:false en cada objeto y rechaza constraints
/// numéricos/de longitud (minimum/maximum, minLength/maxLength…). <see cref="SanitizeForAnthropic"/>
/// adapta el schema compartido antes de enviarlo para que Anthropic no devuelva 400.
/// output_config.format es GA: no requiere beta header y funciona con
/// anthropic-version 2023-06-01. Re-verificado contra
/// platform.claude.com/docs/en/build-with-claude/structured-outputs el 2026-06-17:
/// la nota de migración dice textualmente "beta headers are no longer required" y
/// el header legacy structured-outputs-2025-11-13 solo sobrevive por compatibilidad
/// transitoria. Haiku 4.5 (modelo de este cliente) está en la lista de soportados.
/// stop_reason "refusal" se mapea a content_filtered para que la cadena haga fallback.
/// </summary>
public sealed class AnthropicLlmClient(
    HttpClient httpClient,
    string apiKey,
    string model,
    ILogger logger) : ILlmClient
{
    public string ProviderName => "anthropic";
    public string Model => model;

    public async Task<LlmJsonResponse> GenerateJsonAsync(LlmJsonRequest request, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["max_tokens"] = request.MaxOutputTokens,
            ["temperature"] = request.Temperature,
            ["messages"] = new[] { new { role = "user", content = request.Prompt } },
        };
        if (request.JsonSchema is { } schema)
        {
            var safeSchema = SanitizeForAnthropic(JsonNode.Parse(schema.GetRawText()));
            body["output_config"] = new { format = new { type = "json_schema", schema = safeSchema } };
        }

        var sw = Stopwatch.StartNew();
        string? rawBody = null;
        try
        {
            var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            var httpRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
            httpRequest.Headers.Add("x-api-key", apiKey);
            httpRequest.Headers.Add("anthropic-version", "2023-06-01");
            httpRequest.Content = content;

            var response = await httpClient.SendAsync(httpRequest, ct);
            sw.Stop();
            var latencyMs = (int)sw.ElapsedMilliseconds;

            var responseJson = await response.Content.ReadAsStringAsync(ct);
            rawBody = LlmDiagnostics.TruncateResponse(responseJson);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("LLM[anthropic]: API returned {Status}", (int)response.StatusCode);
                return Failure(request, LlmDiagnostics.TruncateResponse(responseJson), latencyMs,
                    (int)response.StatusCode, "http_error", $"HTTP {(int)response.StatusCode}");
            }

            using var doc = JsonDocument.Parse(responseJson);

            int? inputTokens = null, outputTokens = null;
            if (doc.RootElement.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("input_tokens", out var it)) inputTokens = it.GetInt32();
                if (usage.TryGetProperty("output_tokens", out var ot)) outputTokens = ot.GetInt32();
            }

            string? stopReason = null;
            if (doc.RootElement.TryGetProperty("stop_reason", out var sr)) stopReason = sr.GetString();

            string? text = null;
            if (doc.RootElement.TryGetProperty("content", out var blocks) && blocks.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in blocks.EnumerateArray())
                {
                    if (block.TryGetProperty("type", out var bt) && bt.GetString() == "text"
                        && block.TryGetProperty("text", out var btext))
                    {
                        text = btext.GetString();
                        break;
                    }
                }
            }

            if (stopReason == "refusal" || string.IsNullOrEmpty(text))
            {
                var code = stopReason == "refusal" ? "content_filtered" : "empty_response";
                logger.LogWarning("LLM[anthropic]: unusable response (stop_reason={Reason})", stopReason);
                return Failure(request, LlmDiagnostics.TruncateResponse(responseJson), latencyMs,
                    (int)response.StatusCode, code, $"Unusable response (stop_reason={stopReason})",
                    stopReason, inputTokens, outputTokens);
            }

            var totalTokens = (inputTokens ?? 0) + (outputTokens ?? 0);

            var diag = new AiCallDiagnostics(
                Provider: ProviderName,
                Model: model,
                Prompt: LlmDiagnostics.TruncatePrompt(request.Prompt),
                ResponseRaw: LlmDiagnostics.TruncateResponse(responseJson),
                FinishReason: stopReason,
                LatencyMs: latencyMs,
                InputTokens: inputTokens,
                OutputTokens: outputTokens,
                ThinkingTokens: null,
                TotalTokens: totalTokens > 0 ? totalTokens : null,
                CostUsd: LlmCostCalculator.Calculate(model, inputTokens, outputTokens),
                HttpStatus: (int)response.StatusCode,
                ErrorCode: null,
                ErrorMessage: null);

            return new LlmJsonResponse(text, diag);
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            logger.LogError(ex, "LLM[anthropic]: API call failed");
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
            logger.LogError("LLM[anthropic]: API call timed out");
            return Failure(request, rawBody, (int)sw.ElapsedMilliseconds, null, "timeout", "Anthropic API call timed out");
        }
        catch (JsonException ex)
        {
            // 200 con cuerpo no-JSON (HTML de un proxy/gateway, body truncado): fallo del provider, no excepción.
            sw.Stop();
            logger.LogError(ex, "LLM[anthropic]: malformed JSON response body");
            return Failure(request, rawBody, (int)sw.ElapsedMilliseconds, null, "parse_error", ex.Message);
        }
        catch (Exception ex)
        {
            // Polly TimeoutRejectedException (AttemptTimeout/TotalRequestTimeout) y cualquier otro
            // fallo inesperado: convertir en fallo del provider para que la cadena pruebe el siguiente.
            sw.Stop();
            logger.LogError(ex, "LLM[anthropic]: unexpected error ({Type})", ex.GetType().Name);
            return Failure(request, rawBody, (int)sw.ElapsedMilliseconds, null, "provider_error", ex.Message);
        }
    }

    /// <summary>
    /// Adapta el schema compartido (dialecto Gemini) a lo que acepta el structured output de
    /// Anthropic: cada objeto necesita additionalProperties:false y no se admiten constraints
    /// numéricos/de longitud. Recursivo sobre objetos y arrays. Devuelve el nodo mutado.
    /// </summary>
    private static readonly string[] UnsupportedKeywords =
    [
        "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf",
        "minLength", "maxLength", "pattern", "format", "minItems", "maxItems", "uniqueItems",
    ];

    private static JsonNode? SanitizeForAnthropic(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var keyword in UnsupportedKeywords) obj.Remove(keyword);
                if (obj.TryGetPropertyValue("type", out var t)
                    && t is JsonValue tv && tv.TryGetValue<string>(out var typeName) && typeName == "object"
                    && !obj.ContainsKey("additionalProperties"))
                {
                    obj["additionalProperties"] = false;
                }
                foreach (var kvp in obj) SanitizeForAnthropic(kvp.Value);
                break;
            case JsonArray arr:
                foreach (var item in arr) SanitizeForAnthropic(item);
                break;
        }
        return node;
    }

    private LlmJsonResponse Failure(
        LlmJsonRequest request, string? responseRaw, int latencyMs, int? httpStatus,
        string errorCode, string errorMessage, string? finishReason = null,
        int? inputTokens = null, int? outputTokens = null) =>
        new(null, new AiCallDiagnostics(
            Provider: ProviderName, Model: model,
            Prompt: LlmDiagnostics.TruncatePrompt(request.Prompt),
            ResponseRaw: responseRaw, FinishReason: finishReason, LatencyMs: latencyMs,
            InputTokens: inputTokens, OutputTokens: outputTokens, ThinkingTokens: null, TotalTokens: null,
            CostUsd: null, HttpStatus: httpStatus, ErrorCode: errorCode, ErrorMessage: errorMessage));
}
