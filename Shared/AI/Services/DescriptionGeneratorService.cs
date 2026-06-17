using System.Text;
using System.Text.Json;

namespace LocalList.API.NET.Shared.AI.Services;

/// <summary>
/// Generates short editorial descriptions for places using Gemini 2.5 Flash.
/// Used by bulk import, import-from-urls, suggest-description, and backfill-descriptions.
/// </summary>
public class DescriptionGeneratorService : IDescriptionGeneratorService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _config;
    private readonly ILogger<DescriptionGeneratorService> _logger;

    public DescriptionGeneratorService(HttpClient httpClient, IConfiguration config, ILogger<DescriptionGeneratorService> logger)
    {
        _httpClient = httpClient;
        _config = config;
        _logger = logger;
    }

    public async Task<string?> GeneratePlaceDescriptionAsync(
        string name, string city, string category,
        string? subcategory, IEnumerable<string>? googleTypes,
        decimal? rating, int? reviewCount, string? neighborhood,
        CancellationToken ct = default)
    {
        var result = await GeneratePlaceDescriptionWithDiagnosticsAsync(
            name, city, category, subcategory, googleTypes, rating, reviewCount, neighborhood, ct);
        return result.Description;
    }

    public async Task<GeneratePlaceDescriptionResult> GeneratePlaceDescriptionWithDiagnosticsAsync(
        string name, string city, string category,
        string? subcategory, IEnumerable<string>? googleTypes,
        decimal? rating, int? reviewCount, string? neighborhood,
        CancellationToken ct = default)
    {
        var apiKey = _config["Gemini:ApiKey"];
        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogWarning("Gemini API key missing — cannot generate description for '{Name}'", name);
            return new GeneratePlaceDescriptionResult(null, "missing_key", "Gemini:ApiKey not configured");
        }

        var typeHint = googleTypes is not null ? string.Join(", ", googleTypes) : "";
        var ratingHint = rating.HasValue ? $" Google rating {rating:F1} ({reviewCount} reviews)." : "";
        var hoodHint = !string.IsNullOrEmpty(neighborhood) ? $" Located in {neighborhood}." : "";
        var subHint = !string.IsNullOrEmpty(subcategory) ? $" Subcategory: {subcategory}." : "";

        var prompt = $"""
            Write a single punchy editorial sentence (40-80 words) for a curated travel guide entry.
            The tone is warm, inspiring, first-person-plural ("we"). No em-dashes.
            Do NOT start with the place name. Do NOT include pricing or hours.

            Place: {name}
            City: {city}
            Category: {category}{subHint}{hoodHint}
            Google types: {typeHint}{ratingHint}

            Return ONLY the sentence. No JSON, no quotes, no extra text.
            """;

        var requestBody = new
        {
            contents = new[] { new { parts = new[] { new { text = prompt } } } },
            generationConfig = new
            {
                temperature = 0.7,
                maxOutputTokens = 220,
                responseMimeType = "text/plain"
            }
        };

        try
        {
            var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
            var requestMessage = new HttpRequestMessage(HttpMethod.Post,
                "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent");
            requestMessage.Headers.Add("x-goog-api-key", apiKey);
            requestMessage.Content = content;

            var response = await _httpClient.SendAsync(requestMessage, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                var snippet = errorBody.Length > 300 ? errorBody[..300] : errorBody;
                _logger.LogError("Gemini generate description HTTP {Status} for '{Name}': {Body}", (int)response.StatusCode, name, snippet);
                return new GeneratePlaceDescriptionResult(null, "http_error", $"{(int)response.StatusCode}: {snippet}");
            }

            var responseJson = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(responseJson);
            var root = doc.RootElement;

            if (!root.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
                return new GeneratePlaceDescriptionResult(null, "empty_response", "No candidates in response");

            var candidate = candidates[0];
            if (candidate.TryGetProperty("finishReason", out var finishReason))
            {
                var reason = finishReason.GetString();
                if (reason is "SAFETY" or "RECITATION" or "PROHIBITED_CONTENT")
                    return new GeneratePlaceDescriptionResult(null, "safety_block", $"finishReason={reason}");
            }

            if (!candidate.TryGetProperty("content", out var candidateContent))
                return new GeneratePlaceDescriptionResult(null, "empty_response", "No content in candidate");

            var raw = GetPartsText(candidateContent);
            if (raw == null)
                return new GeneratePlaceDescriptionResult(null, "empty_parts", "Gemini returned empty parts array");

            var text = raw.Trim()
                .Replace("—", "-").Replace("–", "-")
                .Replace("‒", "-").Replace("―", "-");

            if (text.Length > 800) text = text[..800];
            if (string.IsNullOrEmpty(text))
                return new GeneratePlaceDescriptionResult(null, "empty_response", null);

            return new GeneratePlaceDescriptionResult(text, null, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gemini generate description failed for place '{Name}'", name);
            return new GeneratePlaceDescriptionResult(null, "exception", $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // Returns null when Gemini returns an empty parts array (e.g. content-filtered responses).
    private static string? GetPartsText(JsonElement content)
    {
        if (!content.TryGetProperty("parts", out var parts)) return null;
        if (parts.GetArrayLength() == 0) return null;
        return parts[0].TryGetProperty("text", out var t) ? t.GetString() : null;
    }
}

public record GeneratePlaceDescriptionResult(string? Description, string? ErrorKind, string? ErrorMessage);
