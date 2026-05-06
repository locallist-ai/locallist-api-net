using System.Text;
using System.Text.Json;
using LocalList.API.NET.Features.Builder.Shared;
using LocalList.API.NET.Shared.Data.Entities;
using LocalList.API.NET.Shared.Taxonomy;

namespace LocalList.API.NET.Features.Builder;

public class AiProviderService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _config;
    private readonly ILogger<AiProviderService> _logger;

    private static readonly string[] AllowedCategories =
        PlaceTaxonomy.Categories.Select(c => c.ToLowerInvariant()).ToArray();
    private static readonly string[] AllowedGroupTypes = { "solo", "couple", "friends", "family-kids", "family", "group" };

    public AiProviderService(HttpClient httpClient, IConfiguration config, ILogger<AiProviderService> logger)
    {
        _httpClient = httpClient;
        _config = config;
        _logger = logger;
    }

    public async Task<ExtractedPreferences> ExtractPreferencesAsync(string message, TripContextDto? context, string lang = "en", CancellationToken ct = default)
    {
        try
        {
            var apiKey = _config["Gemini:ApiKey"];
            if (string.IsNullOrEmpty(apiKey))
            {
                _logger.LogWarning("Gemini API Key missing. Falling back to keywords.");
                return MergeContextIntoPrefs(ExtractWithKeywords(message, context), context);
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

            var parsed = ParseAiResponse(textResult);
            return MergeContextIntoPrefs(parsed, context);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Gemini API call failed. Falling back to keywords.");
            return MergeContextIntoPrefs(ExtractWithKeywords(message, context), context);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Polly/resilience canceló por timeout (AttemptTimeout o TotalRequestTimeout),
            // no el cliente. Caemos al fallback keyword igual que en error transitorio.
            _logger.LogError("Gemini API call timed out. Falling back to keywords.");
            return MergeContextIntoPrefs(ExtractWithKeywords(message, context), context);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse Gemini response. Falling back to keywords.");
            return MergeContextIntoPrefs(ExtractWithKeywords(message, context), context);
        }
    }

    private string BuildPrompt(string message, TripContextDto? context, string lang = "en")
    {
        // Sanitize user input to mitigate prompt injection.
        var sanitized = message.Replace("\"", "'").Replace("\\", "");
        if (sanitized.Length > 500) sanitized = sanitized[..500];

        var groupType = context?.GroupType ?? "";
        var categories = context?.Categories ?? new List<string>();
        var days = context?.Days?.ToString() ?? "";
        var city = context?.City ?? "";
        var langNote = lang != "en"
            ? $"\n- planName and description MUST be written in {lang} (not English)."
            : "";

        // Prompt construido como constraints MANDATORY para que Gemini respete lo que el
        // usuario ya seleccionó en el wizard. El mensaje libre ("Hola", "lo que sea") solo
        // enriquece, no sobreescribe lo ya elegido.
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

            // WARN si Gemini se salió del rango — señal de prompt-injection o modelo alucinando.
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

            // Dedup post-audit: BuilderController.GeneratePlan ya loguea el echo de prefs
            // a nivel Info ("Builder: prefs days=..."), así que aquí emitimos solo el tag
            // de source para el transcript de observabilidad.
            _logger.LogInformation("Prefs source=gemini (parsed OK)");

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

        // Seed desde context.Categories: el wizard ya capturó los intereses del usuario.
        var contextCats = context?.Categories ?? new List<string>();
        foreach (var cat in contextCats)
        {
            if (!cats.Contains(cat, StringComparer.OrdinalIgnoreCase))
                cats.Add(cat);
        }

        // Si tras los dos intentos seguimos vacíos, defaults sensatos.
        if (cats.Count == 0) cats.AddRange(new[] { "food", "outdoors", "culture" });

        // family-kids: excluir nightlife aunque haya aparecido por keyword en el mensaje.
        var groupType = context?.GroupType ?? "couple";
        if (GroupTypePolicy.IsFamilyContext(groupType))
            cats.RemoveAll(c => string.Equals(c, "nightlife", StringComparison.OrdinalIgnoreCase));

        // Vibes: usar categories como descriptores de vibe para naming.
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

        // Dedup post-audit: BuilderController.GeneratePlan ya loguea el echo de prefs.
        _logger.LogInformation("Prefs source=keyword_fallback");

        return result;
    }

    /// <summary>
    /// Context wins: el wizard ya capturó las elecciones explícitas del usuario;
    /// esas son autoritativas sobre lo que devuelva Gemini (que observado en prod
    /// a veces retorna campos vacíos o defaults ignorando el prompt MANDATORY).
    ///
    /// Se llama post-parse en el happy path y también tras <see cref="ExtractWithKeywords"/>
    /// (para que los 4 paths de <see cref="ExtractPreferencesAsync"/> converjan en el mismo
    /// contrato hacia el resto del pipeline).
    ///
    /// Idempotente: si <paramref name="context"/> es null o ya estaba aplicado, no rompe nada.
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

        // Family excluye nightlife globalmente (complementa la exclusión en ExtractWithKeywords
        // + matrix timeBlock + ScoreSuitableFor; aquí lo hacemos al nivel de categories).
        if (GroupTypePolicy.IsFamilyContext(prefs.GroupType))
        {
            prefs.Categories.RemoveAll(c => string.Equals(c, "nightlife", StringComparison.OrdinalIgnoreCase));
            prefs.MaxStopsPerDay = 3;
        }

        // Categories explícitas del wizard (interests step) — autoritativas:
        // si el usuario marcó food/outdoors/etc directamente, sobrescriben/
        // amplían las derivadas de Preferences. Pablo 2026-04-25.
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

        // Sub-categorías, company/style tags, budget amount — pasan al ranking
        // como soft signals. Audit follow-up 2026-04-27 (C6): cap explícito
        // contra clientes que shippeen dicts/listas grandes para hinchar
        // el coste/latency del pipeline Gemini.
        const int MaxSubcategoryBuckets = 10;
        const int MaxTagsPerBucket = 10;
        const int MaxCompanyOrStyleTags = 10;
        if (context.Subcategories != null && context.Subcategories.Count > 0)
        {
            // Cap claves del dict + tags por bucket. Trunca silenciosamente —
            // no lanzamos 400 porque el cliente legítimo nunca debería pasar
            // de los caps; sólo es defense-in-depth contra requests maliciosos.
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

        _logger.LogInformation(
            "Prefs after context merge: days={Days} categories=[{Cats}] vibes=[{Vibes}] groupType={GT} maxStops={Max}",
            prefs.Days,
            string.Join(",", prefs.Categories),
            string.Join(",", prefs.Vibes),
            prefs.GroupType,
            prefs.MaxStopsPerDay);

        return prefs;
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
        var eSubcategory = EscapeJson(place.Subcategory ?? string.Empty);

        var prompt = $$"""
            Translate the following place fields from English to {{targetLang}}-ES (Spain Spanish).
            Rules:
            - Preserve verbatim (do NOT translate): proper place names, brand names ("LocalList"),
              Miami neighborhoods (Wynwood, Coconut Grove, South Beach, Coral Gables, Brickell,
              Pinecrest, Little Havana, Edgewater, Design District), cuisine names (Cuban, Peruvian,
              American, Italian), and any value of "name" in the input.
            - Maintain an editorial, inspiring, first-person-plural travel tone.
            - bestFor and suitableFor must remain as JSON arrays of strings.
            - Return ONLY valid JSON with the exact keys shown below. No extra text.

            Input:
            {
              "name": "{{eName}}",
              "whyThisPlace": "{{eWhy}}",
              "bestTime": "{{eBestTime}}",
              "neighborhood": "{{eNeighborhood}}",
              "subcategory": "{{eSubcategory}}",
              "bestFor": {{bestForJson}},
              "suitableFor": {{suitableForJson}}
            }

            Output format:
            {
              "name": "...",
              "whyThisPlace": "...",
              "bestTime": "...",
              "neighborhood": "...",
              "subcategory": "...",
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
            var text = doc.RootElement
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text").GetString() ?? "{}";

            using var result = JsonDocument.Parse(text);
            var root = result.RootElement;

            return new PlaceTranslationDraft(
                Name: GetStr(root, "name"),
                WhyThisPlace: GetStr(root, "whyThisPlace"),
                BestTime: GetStr(root, "bestTime"),
                Neighborhood: GetStr(root, "neighborhood"),
                Subcategory: GetStr(root, "subcategory"),
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
            var text = doc.RootElement
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text").GetString() ?? "{}";

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

    private static string EscapeJson(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", "");

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
    string? Subcategory,
    List<string>? BestFor,
    List<string>? SuitableFor
);

public record PlanTranslationDraft(
    string? Name,
    string? Description
);
