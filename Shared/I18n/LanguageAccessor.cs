using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalList.API.NET.Shared.I18n;

public sealed class LanguageAccessor
{
    private readonly IHttpContextAccessor _http;

    public LanguageAccessor(IHttpContextAccessor http) => _http = http;

    public string Language
    {
        get
        {
            var header = _http.HttpContext?.Request.Headers["Accept-Language"].ToString();
            if (!string.IsNullOrEmpty(header))
            {
                var first = header.Split(',')[0].Trim().Split('-')[0].ToLowerInvariant();
                if (first == "es") return "es";
            }
            return "en";
        }
    }

    // Resolves a translated string from a jsonb i18n dict.
    // curated: requires translation_status[lang] == "approved" before serving non-EN.
    // user: serves whatever language exists, fallback to first available value.
    public static string? ResolveString(
        JsonDocument? i18nDoc,
        string lang,
        string? fallback,
        bool isCurated,
        JsonDocument? translationStatusDoc = null)
    {
        if (i18nDoc == null) return fallback;

        if (lang != "en" && isCurated && !IsApproved(translationStatusDoc, lang))
            return GetStringProp(i18nDoc, "en") ?? fallback;

        return GetStringProp(i18nDoc, lang)
            ?? (isCurated
                ? GetStringProp(i18nDoc, "en") ?? fallback
                : FirstAvailableString(i18nDoc) ?? fallback);
    }

    public static List<string>? ResolveStringList(
        JsonDocument? i18nDoc,
        string lang,
        List<string>? fallback,
        bool isCurated,
        JsonDocument? translationStatusDoc = null)
    {
        if (i18nDoc == null) return fallback;

        if (lang != "en" && isCurated && !IsApproved(translationStatusDoc, lang))
            return GetList(i18nDoc, "en") ?? fallback;

        return GetList(i18nDoc, lang)
            ?? (isCurated ? GetList(i18nDoc, "en") ?? fallback : FirstAvailableList(i18nDoc) ?? fallback);
    }

    // Sets a string value for a language key in a jsonb i18n dict. Returns new JsonDocument.
    public static JsonDocument SetI18nString(JsonDocument? current, string lang, string? value)
    {
        var node = current != null
            ? JsonNode.Parse(current.RootElement.GetRawText()) as JsonObject ?? new JsonObject()
            : new JsonObject();
        if (value is null)
            node.Remove(lang);
        else
            node[lang] = JsonValue.Create(value);
        return JsonDocument.Parse(node.ToJsonString());
    }

    // Sets a List<string> value for a language key in a jsonb i18n dict.
    public static JsonDocument SetI18nList(JsonDocument? current, string lang, List<string>? values)
    {
        var node = current != null
            ? JsonNode.Parse(current.RootElement.GetRawText()) as JsonObject ?? new JsonObject()
            : new JsonObject();
        if (values is null)
            node.Remove(lang);
        else
        {
            var arr = new JsonArray();
            foreach (var v in values) arr.Add(JsonValue.Create(v));
            node[lang] = arr;
        }
        return JsonDocument.Parse(node.ToJsonString());
    }

    private static bool IsApproved(JsonDocument? statusDoc, string lang) =>
        statusDoc != null
        && statusDoc.RootElement.TryGetProperty(lang, out var s)
        && s.ValueKind == JsonValueKind.String
        && s.GetString() == "approved";

    private static string? GetStringProp(JsonDocument doc, string key) =>
        doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string? FirstAvailableString(JsonDocument doc)
    {
        foreach (var prop in doc.RootElement.EnumerateObject())
            if (prop.Value.ValueKind == JsonValueKind.String) return prop.Value.GetString();
        return null;
    }

    private static List<string>? GetList(JsonDocument doc, string lang)
    {
        if (!doc.RootElement.TryGetProperty(lang, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return null;
        return arr.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .ToList();
    }

    private static List<string>? FirstAvailableList(JsonDocument doc)
    {
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var result = GetList(doc, prop.Name);
            if (result != null) return result;
        }
        return null;
    }
}
