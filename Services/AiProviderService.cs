using System.Text.Json;
using System.Text.Json.Serialization;
using LocalList.API.NET.Data.Models;
using System.Net.Http.Headers;
using System.Text;

namespace LocalList.API.NET.Services;

public class ExtractedPreferences
{
    public int Days { get; set; } = 1;
    public List<string> Categories { get; set; } = new();
    public List<string> Vibes { get; set; } = new();
    public string GroupType { get; set; } = "couple";
    public string PlanName { get; set; } = "My Plan";
    public int MaxStopsPerDay { get; set; } = 5;
}

public class TripContextDto
{
    public string? GroupType { get; set; }
    public List<string>? Preferences { get; set; }
    public List<string>? Vibes { get; set; }
    public int? Days { get; set; }
    public string? City { get; set; }
}

public class AiProviderService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _config;
    private readonly ILogger<AiProviderService> _logger;

    private static readonly string[] AllowedCategories = { "food", "nightlife", "coffee", "outdoors", "wellness", "culture" };
    private static readonly string[] AllowedGroupTypes = { "solo", "couple", "friends", "family-kids", "family", "group" };

    public AiProviderService(HttpClient httpClient, IConfiguration config, ILogger<AiProviderService> logger)
    {
        _httpClient = httpClient;
        _config = config;
        _logger = logger;
    }

    public async Task<ExtractedPreferences> ExtractPreferencesAsync(string message, TripContextDto? context)
    {
        try
        {
            var apiKey = _config["Gemini:ApiKey"];
            if (string.IsNullOrEmpty(apiKey)) throw new Exception("Gemini API Key missing.");

            var prompt = BuildPrompt(message, context);
            
            var requestBody = new
            {
                contents = new[]
                {
                    new { parts = new[] { new { text = prompt } } }
                },
                generationConfig = new
                {
                    temperature = 0.3,
                    maxOutputTokens = 300,
                    responseMimeType = "application/json"
                }
            };

            var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key={apiKey}";
            
            var response = await _httpClient.PostAsync(url, content);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(responseJson);
            
            var textResult = doc.RootElement
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text").GetString() ?? "{}";

            return ParseAiResponse(textResult);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gemini extraction failed. Falling back to keywords.");
            return ExtractWithKeywords(message, context);
        }
    }

    private string BuildPrompt(string message, TripContextDto? context)
    {
        var ctxStr = JsonSerializer.Serialize(context ?? new TripContextDto());
        return $@"Extract travel plan preferences from this message. Return JSON only, no markdown.
Message: ""{message}""
Context: {ctxStr}

Return this exact JSON shape:
{{
  ""days"": number (1-7, default 1),
  ""categories"": string[] (from: food, nightlife, coffee, outdoors, wellness, culture),
  ""vibes"": string[] (e.g. romantic, adventurous, relaxed, party, cultural),
  ""groupType"": string (solo/couple/friends/family-kids/family/group),
  ""planName"": string (short descriptive name for the plan),
  ""maxStopsPerDay"": number (3-6, based on pace)
}}";
    }

    private ExtractedPreferences ParseAiResponse(string json)
    {
        try
        {
            var cleaned = json.Replace("```json\n", "").Replace("```\n", "").Replace("```", "").Trim();
            var result = JsonSerializer.Deserialize<ExtractedPreferences>(cleaned, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) 
                         ?? new ExtractedPreferences();

            result.Days = Math.Clamp(result.Days, 1, 7);
            result.MaxStopsPerDay = Math.Clamp(result.MaxStopsPerDay, 3, 6);
            
            if (result.Categories != null)
                result.Categories = result.Categories.Where(c => AllowedCategories.Contains(c.ToLower())).ToList();
            else result.Categories = new List<string>();

            if (result.GroupType != null && !AllowedGroupTypes.Contains(result.GroupType.ToLower()))
                result.GroupType = "couple";

            return result;
        }
        catch
        {
            return new ExtractedPreferences();
        }
    }

    private ExtractedPreferences ExtractWithKeywords(string message, TripContextDto? context)
    {
        var lower = message.ToLower();
        var cats = new List<string>();

        if (lower.Contains("food") || lower.Contains("eat") || lower.Contains("restaurant")) cats.Add("food");
        if (lower.Contains("night") || lower.Contains("bar") || lower.Contains("club")) cats.Add("nightlife");
        if (lower.Contains("coffee") || lower.Contains("cafe") || lower.Contains("breakfast")) cats.Add("coffee");
        if (cats.Count == 0) cats.AddRange(new[] { "food", "outdoors", "culture" });

        return new ExtractedPreferences
        {
            Days = context?.Days ?? (lower.Contains("weekend") ? 2 : 1),
            Categories = cats,
            Vibes = context?.Vibes ?? context?.Preferences ?? new List<string>(),
            GroupType = context?.GroupType ?? "couple",
            PlanName = message.Length > 60 ? message.Substring(0, 60) : message,
            MaxStopsPerDay = context?.GroupType == "family-kids" ? 3 : 5
        };
    }
}
