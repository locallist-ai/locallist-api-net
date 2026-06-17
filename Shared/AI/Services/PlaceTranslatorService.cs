using System.Text;
using System.Text.Json;
using LocalList.API.NET.Shared.Data.Entities;

namespace LocalList.API.NET.Shared.AI.Services;

/// <summary>
/// Translates Place and Plan content to a target language using Gemini 2.5 Flash.
/// Used by admin translation endpoints and translate-batch backfill.
/// </summary>
public class PlaceTranslatorService : IPlaceTranslatorService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _config;
    private readonly ILogger<PlaceTranslatorService> _logger;

    public PlaceTranslatorService(HttpClient httpClient, IConfiguration config, ILogger<PlaceTranslatorService> logger)
    {
        _httpClient = httpClient;
        _config = config;
        _logger = logger;
    }

    public async Task<PlaceTranslationDraft?> TranslatePlaceAsync(Place place, string targetLang = "es", CancellationToken ct = default)
    {
        var apiKey = _config["Gemini:ApiKey"];
        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogWarning("Gemini API key missing — cannot translate place {PlaceId}", place.Id);
            return null;
        }

        var bestForJson = place.BestFor is { Count: > 0 } ? JsonSerializer.Serialize(place.BestFor) : "[]";
        var suitableForJson = place.SuitableFor is { Count: > 0 } ? JsonSerializer.Serialize(place.SuitableFor) : "[]";
        var eName = EscapeJson(place.Name);
        var eWhy = EscapeJson(place.WhyThisPlace);
        var eBestTime = EscapeJson(place.BestTime ?? string.Empty);
        var eNeighborhood = EscapeJson(place.Neighborhood ?? string.Empty);
        var subcategoriesJson = place.Subcategories is { Count: > 0 }
            ? JsonSerializer.Serialize(place.Subcategories)
            : "[]";

        var prompt = $$"""
            Translate the following place fields from English to {{targetLang}}-ES (Spain Spanish).
            Rules:
            - Preserve verbatim (do NOT translate): proper place names, brand names ("LocalList"),
              Miami neighborhoods (Wynwood, Coconut Grove, South Beach, Coral Gables, Brickell,
              Pinecrest, Little Havana, Edgewater, Design District), cuisine names (Cuban, Peruvian,
              American, Italian), and any value of "name" in the input.
            - Maintain an editorial, inspiring, first-person-plural travel tone.
            - subcategories, bestFor and suitableFor must remain as JSON arrays of strings.
            - Return ONLY valid JSON with the exact keys shown below. No extra text.

            Input:
            {
              "name": "{{eName}}",
              "whyThisPlace": "{{eWhy}}",
              "bestTime": "{{eBestTime}}",
              "neighborhood": "{{eNeighborhood}}",
              "subcategories": {{subcategoriesJson}},
              "bestFor": {{bestForJson}},
              "suitableFor": {{suitableForJson}}
            }

            Output format:
            {
              "name": "...",
              "whyThisPlace": "...",
              "bestTime": "...",
              "neighborhood": "...",
              "subcategories": [...],
              "bestFor": [...],
              "suitableFor": [...]
            }
            """;

        var requestBody = new
        {
            contents = new[] { new { parts = new[] { new { text = prompt } } } },
            generationConfig = new
            {
                temperature = 0.2,
                maxOutputTokens = 800,
                responseMimeType = "application/json"
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
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(responseJson);
            var geminiContent = doc.RootElement
                .GetProperty("candidates")[0]
                .GetProperty("content");
            var text = GetPartsText(geminiContent) ?? "{}";

            using var result = JsonDocument.Parse(text);
            var root = result.RootElement;

            return new PlaceTranslationDraft(
                Name: GetStr(root, "name"),
                WhyThisPlace: GetStr(root, "whyThisPlace"),
                BestTime: GetStr(root, "bestTime"),
                Neighborhood: GetStr(root, "neighborhood"),
                Subcategories: GetStrList(root, "subcategories"),
                BestFor: GetStrList(root, "bestFor"),
                SuitableFor: GetStrList(root, "suitableFor")
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gemini translate failed for place {PlaceId}", place.Id);
            return null;
        }
    }

    public async Task<PlanTranslationDraft?> TranslatePlanAsync(Plan plan, string targetLang = "es", CancellationToken ct = default)
    {
        var apiKey = _config["Gemini:ApiKey"];
        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogWarning("Gemini API key missing — cannot translate plan {PlanId}", plan.Id);
            return null;
        }

        var eName = EscapeJson(plan.Name);
        var eDesc = EscapeJson(plan.Description ?? string.Empty);

        var prompt = $$"""
            Translate the following travel plan fields from English to {{targetLang}}-ES (Spain Spanish).
            Rules:
            - Preserve verbatim (do NOT translate): city names, place names, brand names ("LocalList"),
              Miami neighborhoods (Wynwood, Coconut Grove, South Beach, Coral Gables, Brickell,
              Pinecrest, Little Havana, Edgewater, Design District).
            - Keep an inspiring, editorial travel tone.
            - Return ONLY valid JSON. No extra text.

            Input:
            {
              "name": "{{eName}}",
              "description": "{{eDesc}}"
            }

            Output format:
            {
              "name": "...",
              "description": "..."
            }
            """;

        var requestBody = new
        {
            contents = new[] { new { parts = new[] { new { text = prompt } } } },
            generationConfig = new
            {
                temperature = 0.2,
                maxOutputTokens = 300,
                responseMimeType = "application/json"
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
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(responseJson);
            var geminiContent = doc.RootElement
                .GetProperty("candidates")[0]
                .GetProperty("content");
            var text = GetPartsText(geminiContent) ?? "{}";

            using var result = JsonDocument.Parse(text);
            var root = result.RootElement;

            return new PlanTranslationDraft(
                Name: GetStr(root, "name"),
                Description: GetStr(root, "description")
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gemini translate failed for plan {PlanId}", plan.Id);
            return null;
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────

    private static string EscapeJson(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", "");

    // Returns null when Gemini returns an empty parts array (e.g. content-filtered responses).
    private static string? GetPartsText(JsonElement content)
    {
        if (!content.TryGetProperty("parts", out var parts)) return null;
        if (parts.GetArrayLength() == 0) return null;
        return parts[0].TryGetProperty("text", out var t) ? t.GetString() : null;
    }

    private static string? GetStr(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static List<string>? GetStrList(JsonElement el, string key)
    {
        if (!el.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        return arr.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .ToList();
    }
}

public record PlaceTranslationDraft(
    string? Name,
    string? WhyThisPlace,
    string? BestTime,
    string? Neighborhood,
    List<string>? Subcategories,
    List<string>? BestFor,
    List<string>? SuitableFor
);

public record PlanTranslationDraft(
    string? Name,
    string? Description
);
