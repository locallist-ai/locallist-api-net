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
    ILogger logger) : ILlmClient
{
    public string ProviderName => "gemini";
    public string Model => model;

    public async Task<LlmJsonResponse> GenerateJsonAsync(LlmJsonRequest request, CancellationToken ct = default)
    {
        object generationConfig = request.JsonSchema is { } schema
            ? new
            {
                temperature = request.Temperature,
                maxOutputTokens = request.MaxOutputTokens,
                responseMimeType = "application/json",
                responseSchema = schema,
            }
            : new
            {
                temperature = request.Temperature,
                maxOutputTokens = request.MaxOutputTokens,
                responseMimeType = "application/json",
            };

        var requestBody = new
        {
            contents = new[] { new { parts = new[] { new { text = request.Prompt } } } },
            generationConfig,
        };

        var sw = Stopwatch.StartNew();
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
                CostUsd: LlmCostCalculator.Calculate(model, inputTokens, outputTokens),
                HttpStatus: (int)response.StatusCode,
                ErrorCode: null,
                ErrorMessage: null);

            return new LlmJsonResponse(text, diag);
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            logger.LogError(ex, "LLM[gemini]: API call failed");
            return Failure(request, null, (int)sw.ElapsedMilliseconds, null, "http_error", ex.Message);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            logger.LogError("LLM[gemini]: API call timed out");
            return Failure(request, null, (int)sw.ElapsedMilliseconds, null, "timeout", "Gemini API call timed out");
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
