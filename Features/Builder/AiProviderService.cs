using System.Text;
using System.Text.Json;

namespace LocalList.API.NET.Features.Builder;

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

    public async Task<ExtractedPreferences> ExtractPreferencesAsync(string message, TripContextDto? context, CancellationToken ct = default)
    {
        try
        {
            var apiKey = _config["Gemini:ApiKey"];
            if (string.IsNullOrEmpty(apiKey))
            {
                _logger.LogWarning("Gemini API Key missing. Falling back to keywords.");
                return ExtractWithKeywords(message, context);
            }

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
            var url = "https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent";

            var requestMessage = new HttpRequestMessage(HttpMethod.Post, url);
            requestMessage.Headers.Add("x-goog-api-key", apiKey); // A6: Secure key transit
            requestMessage.Content = content;

            var response = await _httpClient.SendAsync(requestMessage, ct);
            response.EnsureSuccessStatusCode();

            var responseJson = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(responseJson);

            var textResult = doc.RootElement
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text").GetString() ?? "{}";

            _logger.LogInformation(
                "Gemini raw extracted text: {Preview}",
                textResult.Length > 500 ? textResult[..500] + "…" : textResult);

            return ParseAiResponse(textResult);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Gemini API call failed. Falling back to keywords.");
            return ExtractWithKeywords(message, context);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Polly/resilience canceló por timeout (AttemptTimeout o TotalRequestTimeout),
            // no el cliente. Caemos al fallback keyword igual que en error transitorio.
            _logger.LogError("Gemini API call timed out. Falling back to keywords.");
            return ExtractWithKeywords(message, context);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse Gemini response. Falling back to keywords.");
            return ExtractWithKeywords(message, context);
        }
    }

    private string BuildPrompt(string message, TripContextDto? context)
    {
        // Sanitize user input to mitigate prompt injection.
        var sanitized = message.Replace("\"", "'").Replace("\\", "");
        if (sanitized.Length > 500) sanitized = sanitized[..500];

        var groupType = context?.GroupType ?? "";
        var prefs = context?.Preferences ?? new List<string>();
        var vibes = context?.Vibes ?? new List<string>();
        var days = context?.Days?.ToString() ?? "";
        var city = context?.City ?? "";

        // Prompt construido como constraints MANDATORY para que Gemini respete lo que el
        // usuario ya seleccionó en el wizard. El mensaje libre ("Hola", "lo que sea") solo
        // enriquece, no sobreescribe lo ya elegido.
        return $@"You generate preferences for a travel plan. The user has ALREADY selected
these MANDATORY constraints; you MUST respect them:

- groupType: {groupType} (if family/family-kids: NEVER include ""nightlife"" in categories; prefer family-friendly places)
- duration: {days} days
- user preferences: {string.Join(",", prefs)} (merge into vibes AND categories; these MUST appear)
- user vibes: {string.Join(",", vibes)} (merge into vibes; these MUST appear)
- city: {city}

User's free-text message: ""{sanitized}""

Rules for planName:
- MUST be a short descriptive phrase combining city + duration + 1-2 vibes (e.g. ""Family-friendly Miami weekend"", ""2-day adventure in Miami"").
- DO NOT copy the user's message verbatim.
- DO NOT use greetings like ""Hola"", ""Hi"", ""Hello"" as the plan name.
- If the message is trivial or empty, synthesize planName from city + duration + vibes.

Return JSON only, no markdown. EXACT shape:
{{
  ""days"": number (1-7, default 1),
  ""categories"": string[] (from: food, nightlife, coffee, outdoors, wellness, culture),
  ""vibes"": string[] (e.g. romantic, adventurous, relaxed, party, cultural),
  ""groupType"": string (solo/couple/friends/family-kids/family/group),
  ""planName"": string (following the rules above),
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

            _logger.LogInformation(
                "Prefs source=gemini days={Days} categories=[{Cats}] vibes=[{Vibes}] groupType={GT} planName='{Name}' maxStops={Max}",
                result.Days,
                string.Join(",", result.Categories ?? new List<string>()),
                string.Join(",", result.Vibes ?? new List<string>()),
                result.GroupType,
                result.PlanName,
                result.MaxStopsPerDay);

            return result;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize AI response JSON");
            return new ExtractedPreferences();
        }
    }

    // Mapa fijo preference → categories. Se usa cuando Gemini no está disponible y tenemos
    // que derivar algo razonable del tripContext seleccionado en el wizard.
    private static readonly Dictionary<string, string[]> PreferenceToCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["adventure"] = new[] { "outdoors", "culture" },
        ["relax"] = new[] { "wellness", "coffee" },
        ["cultural"] = new[] { "culture" },
        ["foodie"] = new[] { "food", "coffee" },
        ["nightlife"] = new[] { "nightlife" },
    };

    private ExtractedPreferences ExtractWithKeywords(string message, TripContextDto? context)
    {
        var lower = message.ToLower();
        var cats = new List<string>();

        if (lower.Contains("food") || lower.Contains("eat") || lower.Contains("restaurant")) cats.Add("food");
        if (lower.Contains("night") || lower.Contains("bar") || lower.Contains("club")) cats.Add("nightlife");
        if (lower.Contains("coffee") || lower.Contains("cafe") || lower.Contains("breakfast")) cats.Add("coffee");

        // Seed desde context.Preferences: el wizard ya pasó "adventure"/"relax"/"cultural".
        // Sin esto, un free-text vacío o saludo ("Hola") caía a defaults genéricos y
        // ignoraba la intención del usuario.
        var contextPrefs = context?.Preferences ?? new List<string>();
        foreach (var pref in contextPrefs)
        {
            if (PreferenceToCategories.TryGetValue(pref, out var mapped))
            {
                foreach (var c in mapped)
                {
                    if (!cats.Contains(c, StringComparer.OrdinalIgnoreCase))
                        cats.Add(c);
                }
            }
        }

        // Si tras los dos intentos seguimos vacíos, defaults sensatos.
        if (cats.Count == 0) cats.AddRange(new[] { "food", "outdoors", "culture" });

        // family-kids: excluir nightlife aunque haya aparecido por keyword en el mensaje.
        var groupType = context?.GroupType ?? "couple";
        if (groupType is "family" or "family-kids")
            cats.RemoveAll(c => string.Equals(c, "nightlife", StringComparison.OrdinalIgnoreCase));

        // Vibes: merge contextual vibes + preferences (los preferences funcionan como vibes también).
        var vibes = new List<string>();
        if (context?.Vibes != null) vibes.AddRange(context.Vibes);
        foreach (var pref in contextPrefs)
            if (!vibes.Contains(pref, StringComparer.OrdinalIgnoreCase))
                vibes.Add(pref);

        var result = new ExtractedPreferences
        {
            Days = context?.Days ?? (lower.Contains("weekend") ? 2 : 1),
            Categories = cats,
            Vibes = vibes,
            GroupType = groupType,
            PlanName = message.Length > 60 ? message.Substring(0, 60) : message,
            MaxStopsPerDay = groupType is "family" or "family-kids" ? 3 : 5
        };

        _logger.LogInformation(
            "Prefs source=keyword_fallback days={Days} categories=[{Cats}] vibes=[{Vibes}] groupType={GT} planName='{Name}' maxStops={Max}",
            result.Days,
            string.Join(",", result.Categories),
            string.Join(",", result.Vibes ?? new List<string>()),
            result.GroupType,
            result.PlanName,
            result.MaxStopsPerDay);

        return result;
    }
}
