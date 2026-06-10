using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LocalList.API.NET.Features.Builder.Shared;
using LocalList.API.NET.Shared.Observability;
using LocalList.API.NET.Shared.Taxonomy;

namespace LocalList.API.NET.Features.Builder.Services;

/// <summary>
/// Extracts structured travel preferences from a free-text message and a wizard context,
/// using Gemini 2.5 Flash with keyword fallback.
/// Owned by the builder pipeline — the only caller is PlanGenerationService.
/// </summary>
public class PreferenceExtractorService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _config;
    private readonly ILogger<PreferenceExtractorService> _logger;

    private static readonly string[] AllowedCategories =
        PlaceTaxonomy.Categories.Select(c => c.ToLowerInvariant()).ToArray();
    private static readonly string[] AllowedGroupTypes = { "solo", "couple", "friends", "family-kids", "family", "group" };

    private static readonly Dictionary<string, string[]> PreferenceToCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["adventure"] = new[] { "outdoors", "culture" },
        ["relax"]     = new[] { "wellness", "coffee" },
        ["cultural"]  = new[] { "culture" },
        ["foodie"]    = new[] { "food", "coffee" },
        ["nightlife"] = new[] { "nightlife" },
    };

    public PreferenceExtractorService(HttpClient httpClient, IConfiguration config, ILogger<PreferenceExtractorService> logger)
    {
        _httpClient = httpClient;
        _config = config;
        _logger = logger;
    }

    public async Task<(ExtractedPreferences Prefs, AiCallDiagnostics? Diagnostics)> ExtractPreferencesAsync(
        string message, TripContextDto? context, string lang = "en", CancellationToken ct = default)
    {
        var apiKey = _config["Gemini:ApiKey"];
        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogWarning("Gemini API Key missing. Falling back to keywords.");
            var missingKeyDiag = new AiCallDiagnostics(
                Prompt: string.Empty, ResponseRaw: null, FinishReason: null,
                LatencyMs: 0, InputTokens: null, OutputTokens: null, ThinkingTokens: null, TotalTokens: null,
                CostUsd: null, GeminiStatus: null, ErrorCode: "missing_key", ErrorMessage: "Gemini API key not configured");
            return (MergeContextIntoPrefs(ExtractWithKeywords(message, context), context), missingKeyDiag);
        }

        var prompt = BuildPrompt(message, context, lang);

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
        requestMessage.Headers.Add("x-goog-api-key", apiKey);
        requestMessage.Content = content;

        var sw = Stopwatch.StartNew();
        try
        {
            var response = await _httpClient.SendAsync(requestMessage, ct);
            sw.Stop();
            var latencyMs = (int)sw.ElapsedMilliseconds;

            var responseJson = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Builder: Gemini API returned {Status}", (int)response.StatusCode);
                var httpErrDiag = new AiCallDiagnostics(
                    Prompt: TruncatePrompt(prompt),
                    ResponseRaw: TruncateResponse(responseJson),
                    FinishReason: null, LatencyMs: latencyMs,
                    InputTokens: null, OutputTokens: null, ThinkingTokens: null, TotalTokens: null,
                    CostUsd: null, GeminiStatus: (int)response.StatusCode,
                    ErrorCode: "http_error", ErrorMessage: $"HTTP {(int)response.StatusCode}");
                return (MergeContextIntoPrefs(ExtractWithKeywords(message, context), context), httpErrDiag);
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

            var textResult = doc.RootElement
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text").GetString() ?? "{}";

            _logger.LogInformation(
                "Gemini raw extracted text: {Preview}",
                textResult.Length > 500 ? textResult[..500] + "…" : textResult);

            var diag = new AiCallDiagnostics(
                Prompt: TruncatePrompt(prompt),
                ResponseRaw: TruncateResponse(responseJson),
                FinishReason: finishReason,
                LatencyMs: latencyMs,
                InputTokens: inputTokens,
                OutputTokens: outputTokens,
                ThinkingTokens: thinkingTokens,
                TotalTokens: totalTokens > 0 ? totalTokens : null,
                CostUsd: GeminiCostCalculator.Calculate(inputTokens, outputTokens),
                GeminiStatus: (int)response.StatusCode,
                ErrorCode: null,
                ErrorMessage: null);

            var parsed = ParseAiResponse(textResult);
            return (MergeContextIntoPrefs(parsed, context), diag);
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            _logger.LogError(ex, "Gemini API call failed. Falling back to keywords.");
            var exDiag = new AiCallDiagnostics(
                Prompt: TruncatePrompt(prompt), ResponseRaw: null, FinishReason: null,
                LatencyMs: (int)sw.ElapsedMilliseconds,
                InputTokens: null, OutputTokens: null, ThinkingTokens: null, TotalTokens: null,
                CostUsd: null, GeminiStatus: null, ErrorCode: "http_error", ErrorMessage: ex.Message);
            return (MergeContextIntoPrefs(ExtractWithKeywords(message, context), context), exDiag);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            _logger.LogError("Gemini API call timed out. Falling back to keywords.");
            var toDiag = new AiCallDiagnostics(
                Prompt: TruncatePrompt(prompt), ResponseRaw: null, FinishReason: null,
                LatencyMs: (int)sw.ElapsedMilliseconds,
                InputTokens: null, OutputTokens: null, ThinkingTokens: null, TotalTokens: null,
                CostUsd: null, GeminiStatus: null, ErrorCode: "timeout", ErrorMessage: "Gemini API call timed out");
            return (MergeContextIntoPrefs(ExtractWithKeywords(message, context), context), toDiag);
        }
        catch (JsonException ex)
        {
            sw.Stop();
            _logger.LogError(ex, "Failed to parse Gemini response. Falling back to keywords.");
            var jsonDiag = new AiCallDiagnostics(
                Prompt: TruncatePrompt(prompt), ResponseRaw: null, FinishReason: null,
                LatencyMs: (int)sw.ElapsedMilliseconds,
                InputTokens: null, OutputTokens: null, ThinkingTokens: null, TotalTokens: null,
                CostUsd: null, GeminiStatus: null, ErrorCode: "parse_error", ErrorMessage: ex.Message);
            return (MergeContextIntoPrefs(ExtractWithKeywords(message, context), context), jsonDiag);
        }
    }

    /// <summary>
    /// Context wins: el wizard ya capturó las elecciones explícitas del usuario;
    /// esas son autoritativas sobre lo que devuelva Gemini (que en prod
    /// a veces retorna campos vacíos o defaults ignorando el prompt MANDATORY).
    ///
    /// Se llama post-parse en el happy path y también tras ExtractWithKeywords
    /// para que los 4 paths de ExtractPreferencesAsync converjan en el mismo
    /// contrato hacia el resto del pipeline.
    ///
    /// Idempotente: si context es null o ya estaba aplicado, no rompe nada.
    /// </summary>
    public ExtractedPreferences MergeContextIntoPrefs(ExtractedPreferences prefs, TripContextDto? context)
    {
        if (context == null) return prefs;

        // Days autoritativo desde el wizard.
        if (context.Days.HasValue)
            prefs.Days = Math.Clamp(context.Days.Value, 1, 7);

        // GroupType autoritativo desde el wizard (si el valor está en whitelist).
        if (!string.IsNullOrWhiteSpace(context.GroupType)
            && AllowedGroupTypes.Contains(context.GroupType.ToLowerInvariant()))
        {
            prefs.GroupType = context.GroupType;
        }

        prefs.Categories ??= new List<string>();
        prefs.Vibes ??= new List<string>();

        // Family excluye nightlife globalmente
        if (GroupTypePolicy.IsFamilyContext(prefs.GroupType))
        {
            prefs.Categories.RemoveAll(c => string.Equals(c, "nightlife", StringComparison.OrdinalIgnoreCase));
            prefs.MaxStopsPerDay = 3;
        }

        // Categories explícitas del wizard — autoritativas
        if (context.Categories != null && context.Categories.Count > 0)
        {
            foreach (var cat in context.Categories)
            {
                if (!string.IsNullOrWhiteSpace(cat)
                    && !prefs.Categories.Contains(cat, StringComparer.OrdinalIgnoreCase))
                {
                    prefs.Categories.Add(cat);
                }
            }
        }

        // Sub-categorías, company/style tags, budget amount — soft signals al ranking.
        // Cap explícito (audit C6) contra clientes que envíen dicts/listas grandes.
        const int MaxSubcategoryBuckets = 10;
        const int MaxTagsPerBucket = 10;
        const int MaxCompanyOrStyleTags = 10;
        if (context.Subcategories != null && context.Subcategories.Count > 0)
        {
            var capped = context.Subcategories
                .Take(MaxSubcategoryBuckets)
                .ToDictionary(
                    kv => kv.Key,
                    kv => (kv.Value ?? new List<string>()).Take(MaxTagsPerBucket).ToList());
            prefs.Subcategories = capped;
        }
        if (context.CompanyTags != null && context.CompanyTags.Count > 0)
            prefs.CompanyTags = context.CompanyTags.Take(MaxCompanyOrStyleTags).ToList();
        if (context.BudgetAmount.HasValue && context.BudgetAmount.Value > 0)
            prefs.BudgetAmount = context.BudgetAmount.Value;

        // Pace clamp — authoritative over Gemini's MaxStopsPerDay suggestion
        if (!string.IsNullOrWhiteSpace(context.Pace))
        {
            prefs.Pace = context.Pace.ToLowerInvariant();
            prefs.MaxStopsPerDay = prefs.Pace switch
            {
                "slow" => Math.Min(prefs.MaxStopsPerDay, 3),
                "fast" => Math.Max(prefs.MaxStopsPerDay, 5),
                _      => prefs.MaxStopsPerDay
            };
        }

        // Dietary restrictions — passed through to SchedulingService
        if (context.Dietary != null && context.Dietary.Count > 0)
            prefs.Dietary = context.Dietary.Take(5).ToList();

        // Exclusions — passed through to SchedulingService
        if (context.Exclusions != null && context.Exclusions.Count > 0)
            prefs.Exclusions = context.Exclusions.Take(5).ToList();

        // VibesPrimary → added to Vibes list for the RAG embedding query
        if (!string.IsNullOrWhiteSpace(context.VibesPrimary))
        {
            if (!prefs.Vibes.Contains(context.VibesPrimary, StringComparer.OrdinalIgnoreCase))
                prefs.Vibes.Add(context.VibesPrimary);
        }

        _logger.LogInformation(
            "Prefs after context merge: days={Days} categories=[{Cats}] vibes=[{Vibes}] groupType={GT} maxStops={Max} pace={Pace} exclusions=[{Ex}]",
            prefs.Days,
            string.Join(",", prefs.Categories),
            string.Join(",", prefs.Vibes),
            prefs.GroupType,
            prefs.MaxStopsPerDay,
            prefs.Pace ?? "(none)",
            prefs.Exclusions != null ? string.Join(",", prefs.Exclusions) : "(none)");

        return prefs;
    }

    // ── Private helpers ───────────────────────────────────────────────────

    private static string TruncatePrompt(string prompt) =>
        prompt.Length <= 4096 ? prompt : prompt[..4096];

    private static string TruncateResponse(string response) =>
        response.Length <= 8192 ? response : response[..8192];

    private string BuildPrompt(string message, TripContextDto? context, string lang = "en")
    {
        var sanitized = message.Replace("\"", "'").Replace("\\", "");
        if (sanitized.Length > 500) sanitized = sanitized[..500];

        var groupType = context?.GroupType ?? "";
        var categories = context?.Categories ?? new List<string>();
        var days = context?.Days?.ToString() ?? "";
        var city = context?.City ?? "";
        var langNote = lang != "en"
            ? $"\n- planName and description MUST be written in {lang} (not English)."
            : "";

        return $@"You generate preferences for a travel plan. The user has ALREADY selected
these MANDATORY constraints; you MUST respect them:

- groupType: {groupType} (if family/family-kids: NEVER include ""nightlife"" in categories; prefer family-friendly places)
- duration: {days} days
- user interests: {string.Join(",", categories)} (MUST appear in categories output)
- city: {city}

User's free-text message: ""{sanitized}""

Rules for planName:{langNote}
- MUST be a short descriptive phrase combining city + duration + 1-2 vibes (e.g. ""Family-friendly Miami weekend"", ""2-day adventure in Miami"").
- DO NOT copy the user's message verbatim.
- DO NOT use greetings like ""Hola"", ""Hi"", ""Hello"" as the plan name.
- If the message is trivial or empty, synthesize planName from city + duration + vibes.

Return JSON only, no markdown. EXACT shape:
{{
  ""days"": number (1-7, default 1),
  ""categories"": string[] (from: {string.Join(", ", AllowedCategories)}),
  ""vibes"": string[] (e.g. romantic, adventurous, relaxed, party, cultural),
  ""groupType"": string (solo/couple/friends/family-kids/family/group),
  ""planName"": string (following the rules above),
  ""description"": string (one sentence describing the plan's mood and main activities),
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

            if (result.Days < 1 || result.Days > 7)
                _logger.LogWarning("Gemini Days out of range: {Days} (clamping to 1-7)", result.Days);
            if (result.MaxStopsPerDay < 3 || result.MaxStopsPerDay > 6)
                _logger.LogWarning("Gemini MaxStopsPerDay out of range: {Max} (clamping to 3-6)", result.MaxStopsPerDay);

            result.Days = Math.Clamp(result.Days, 1, 7);
            result.MaxStopsPerDay = Math.Clamp(result.MaxStopsPerDay, 3, 6);

            if (result.Categories != null)
                result.Categories = result.Categories.Where(c => AllowedCategories.Contains(c.ToLower())).ToList();
            else result.Categories = new List<string>();

            if (result.GroupType != null && !AllowedGroupTypes.Contains(result.GroupType.ToLower()))
                result.GroupType = "couple";

            _logger.LogInformation("Prefs source=gemini (parsed OK)");

            return result;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to deserialize AI response JSON");
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

        var contextCats = context?.Categories ?? new List<string>();
        foreach (var cat in contextCats)
        {
            if (!cats.Contains(cat, StringComparer.OrdinalIgnoreCase))
                cats.Add(cat);
        }

        if (cats.Count == 0) cats.AddRange(new[] { "food", "outdoors", "culture" });

        var groupType = context?.GroupType ?? "couple";
        if (GroupTypePolicy.IsFamilyContext(groupType))
            cats.RemoveAll(c => string.Equals(c, "nightlife", StringComparison.OrdinalIgnoreCase));

        var vibes = new List<string>(contextCats);

        var result = new ExtractedPreferences
        {
            Days = context?.Days ?? (lower.Contains("weekend") ? 2 : 1),
            Categories = cats,
            Vibes = vibes,
            GroupType = groupType,
            PlanName = message.Length > 60 ? message.Substring(0, 60) : message,
            MaxStopsPerDay = GroupTypePolicy.IsFamilyContext(groupType) ? 3 : 5
        };

        _logger.LogInformation("Prefs source=keyword_fallback");

        return result;
    }
}
