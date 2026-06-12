using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LocalList.API.NET.Shared.Observability;

namespace LocalList.API.NET.Shared.AI.Llm.Providers;

/// <summary>
/// Provider Anthropic (POST /v1/messages, Claude Haiku 4.5).
/// Structured output vía output_config.format si la request trae JsonSchema
/// (el schema debe llevar additionalProperties:false y no usar rangos numéricos).
/// output_config.format es GA: no requiere beta header y funciona con
/// anthropic-version 2023-06-01 (verificado contra
/// platform.claude.com/docs/en/build-with-claude/structured-outputs, 2026-06-12;
/// el header beta legacy structured-outputs-2025-11-13 ya no es necesario).
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
            body["output_config"] = new { format = new { type = "json_schema", schema } };
        }

        var sw = Stopwatch.StartNew();
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
            return Failure(request, null, (int)sw.ElapsedMilliseconds, null, "http_error", ex.Message);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            logger.LogError("LLM[anthropic]: API call timed out");
            return Failure(request, null, (int)sw.ElapsedMilliseconds, null, "timeout", "Anthropic API call timed out");
        }
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
